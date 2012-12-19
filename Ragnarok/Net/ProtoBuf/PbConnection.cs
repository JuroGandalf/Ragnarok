﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Web;
using System.Threading;
using ProtoBuf;

namespace Ragnarok.Net.ProtoBuf
{
    using Ragnarok.Utility;

    /// <summary>
    /// 受信したコマンドを処理するハンドラ型です。
    /// </summary>
    public delegate void PbCommandHandler<TCmd>(
        object sender, PbCommandEventArgs<TCmd> e);

    /// <summary>
    /// 受信したリクエストを処理するハンドラ型です。
    /// </summary>
    public delegate void PbRequestHandler<TReq, TRes>(
        object sender, PbRequestEventArgs<TReq, TRes> e);

    /// <summary>
    /// 取得したレスポンスを処理するハンドラ型です。
    /// </summary>
    public delegate void PbResponseHandler<TRes>(
        object sender, PbResponseEventArgs<TRes> e);

    /// <summary>
    /// ProtoBufによるデータ送受信を行うクラスです。
    /// </summary>
    /// <remarks>
    /// <see cref="AddCommandHandler{TCmd}"/>や
    /// <see cref="AddRequestHandler{TReq,TRes}"/>に
    /// 登録してあるコマンド(返信のない要求)やリクエスト(返信のある要求)を
    /// 処理します。
    /// 
    /// コマンド・リクエスト・レスポンスともにアプリケーションレベルの
    /// 応答確認を行い、もし正しく送信できていなかった場合は
    /// ３回までの再送要求を行います。
    /// データ送信指示順序とデータ到着順序が同じになるとは限りません。
    /// 
    /// また、プロトコルのバージョンチェックも行うことが可能です。
    /// 必要であればプロトコルのバージョンチェック要求を
    /// 接続開始時に送信し、相手方とのバージョンミスマッチを確認します。
    /// </remarks>
    /// 
    /// <seealso cref="PbPacketHeader"/>
    public class PbConnection : Connection
    {
        /// <summary>
        /// 各種リクエストなどのハンドラです。
        /// </summary>
        private sealed class HandlerInfo
        {
            /// <summary>
            /// 処理するメッセージの型を取得または設定します。
            /// </summary>
            public Type Type
            {
                get;
                set;
            }

            /// <summary>
            /// もしリクエストなら、そのレスポンスの型を取得または設定します。
            /// </summary>
            public Type ResponseType
            {
                get;
                set;
            }

            /// <summary>
            /// リクエストを処理するためのハンドラかどうかを取得します。
            /// </summary>
            public bool IsRequestHandler
            {
                get { return (ResponseType != null); }
            }

            /// <summary>
            /// 実際の処理を行うハンドラを取得または設定します。
            /// </summary>
            public Func<object, IPbResponse> Handler
            {
                get;
                set;
            }

            /// <summary>
            /// ログ出力を行うかどうかを取得または設定します。
            /// </summary>
            public bool IsOutLog
            {
                get;
                set;
            }
        }

        /// <summary>
        /// データIDとレスポンスかどうかで送受信データの一意性を保証します。
        /// </summary>
        private sealed class DataId : IEquatable<DataId>
        {
            public int Id
            {
                get;
                private set;
            }

            public bool IsResponse
            {
                get;
                private set;
            }

            public override bool Equals(object obj)
            {
                return Equals(obj as DataId);
            }

            public bool Equals(DataId other)
            {
                if ((object)other == null)
                {
                    return false;
                }

                return (Id == other.Id && IsResponse == other.IsResponse);
            }

            public override int GetHashCode()
            {
                return (Id.GetHashCode() ^ IsResponse.GetHashCode());
            }

            public DataId(int id, bool isResponse)
            {
                Id = id;
                IsResponse = isResponse;
            }
        }

        /// <summary>
        /// ACKによる応答確認が必要なデータを保持します。
        /// </summary>
        private sealed class NeedAckInfo
        {
            private int tryCount = 0;

            public PbSendData SendData
            {
                get;
                set;
            }

            public int IncrementTryCount()
            {
                return Interlocked.Increment(ref this.tryCount);
            }
        }

        private static readonly PbSendData AckData;
        private static readonly PbSendData NakData;
        private static readonly Dictionary<string, Type> typeCache =
            new Dictionary<string, Type>();
        private readonly object receiveLock = new object();
        private int idCounter = 0;
        private PbPacketHeader packetHeader = new PbPacketHeader();
        private MemoryStream headerStream;
        private MemoryStream typenameStream;
        private MemoryStream payloadStream;
        private readonly Dictionary<int, PbRequestData> requestDataDic =
            new Dictionary<int, PbRequestData>();
        private readonly Dictionary<DataId, NeedAckInfo> needAckDic =
            new Dictionary<DataId, NeedAckInfo>();
        private readonly Dictionary<Type, HandlerInfo> handlerDic =
            new Dictionary<Type, HandlerInfo>();

        /// <summary>
        /// ACKを受信するまでのタイムアウト時間を取得または設定します。
        /// </summary>
        public TimeSpan AckTimeout
        {
            get;
            set;
        }

        /// <summary>
        /// レスポンス応答のデフォルトタイムアウト時間を取得または設定します。
        /// </summary>
        public TimeSpan DefaultRequestTimeout
        {
            get;
            set;
        }

        /// <summary>
        /// プロトコルのバージョンを取得または設定します。
        /// </summary>
        public PbProtocolVersion ProtocolVersion
        {
            get;
            set;
        }

        /// <summary>
        /// T型のメッセージを処理するハンドラを追加します。
        /// </summary>
        public void AddCommandHandler<TCmd>(PbCommandHandler<TCmd> handler,
                                            bool isOutLog = true)
        {
            if (handler == null)
            {
                return;
            }

            lock (this.handlerDic)
            {
                var handlerInfo = new HandlerInfo()
                {
                    Type = typeof(TCmd),
                    ResponseType = null,
                    Handler = (_ =>
                        HandleCommandInternal(handler, _)),
                    IsOutLog = isOutLog,
                };

                this.handlerDic.Add(typeof(TCmd), handlerInfo);
            }
        }

        /// <summary>
        /// コマンドを型付けするためのメソッドです。
        /// </summary>
        private IPbResponse HandleCommandInternal<TCmd>(PbCommandHandler<TCmd>
                                                        handler,
                                                        object commandObj)
        {
            var e = new PbCommandEventArgs<TCmd>((TCmd)commandObj);
            handler(this, e);
            
            return null; // 戻り値はありません。
        }

        /// <summary>
        /// T型のリクエストを処理するハンドラを追加します。
        /// </summary>
        public void AddRequestHandler<TReq, TRes>(PbRequestHandler<TReq, TRes>
                                                  handler,
                                                  bool isOutLog = true)
            where TRes: class
        {
            if (handler == null)
            {
                return;
            }

            lock (this.handlerDic)
            {
                var handlerInfo = new HandlerInfo()
                {
                    Type = typeof(TReq),
                    ResponseType = typeof(TRes),
                    Handler = (requestObj) =>
                        HandleRequestInternal(handler, requestObj),
                    IsOutLog = isOutLog,
                };
                
                this.handlerDic.Add(typeof(TReq), handlerInfo);
            }
        }

        /// <summary>
        /// リクエストを型付けするためのメソッドです。
        /// </summary>
        private IPbResponse HandleRequestInternal<TReq, TRes>(
            PbRequestHandler<TReq, TRes> handler,
            object requestObj)
            where TRes: class
        {
            var e = new PbRequestEventArgs<TReq, TRes>((TReq)requestObj);
            handler(this, e);

            return new PbResponse<TRes>()
            {
                Response = e.Response,
                ErrorCode = e.ErrorCode,
            };
        }

        /// <summary>
        /// T型を処理するハンドラを削除します。
        /// </summary>
        public bool RemoveHandler<T>()
        {
            lock (this.handlerDic)
            {
                return this.handlerDic.Remove(typeof(T));
            }
        }

        /// <summary>
        /// 通信プロトコルのバージョンを調べます。
        /// </summary>
        public PbVersionCheckResult CheckProtocolVersion(TimeSpan timeout)
        {
            if (ProtocolVersion == null)
            {
                throw new PbException("ProtocolVersionがnullです。");
            }

            // 待機用イベントを使い、非同期で確認を行います。
            using (var ev = new AutoResetEvent(false))
            {
                var request = new PbCheckProtocolVersionRequest(ProtocolVersion);
                var result = PbVersionCheckResult.Unknown;

                // 型を指定しないといくつかのコンパイラではコンパイルに失敗します。
                SendRequest<PbCheckProtocolVersionRequest,
                            PbCheckProtocolVersionResponse>(
                    request,
                    timeout,
                    (object sender,
                     PbResponseEventArgs<PbCheckProtocolVersionResponse> e) =>
                    {
                        // プロトコルのバージョンチェックの結果を受け取ります。
                        result = (e.Response == null ?
                            (e.ErrorCode == PbErrorCode.Timeout ?
                             PbVersionCheckResult.Timeout :
                             PbVersionCheckResult.Unknown) :
                            e.Response.Result);

                        ev.Set();
                    });

                ev.WaitOne();
                return result;
            }
        }

        /// <summary>
        /// プロトコルのバージョンチェックを行います。
        /// </summary>
        private void HandleCheckProtocolVersionRequest(
            object sender,
            PbRequestEventArgs<PbCheckProtocolVersionRequest,
                               PbCheckProtocolVersionResponse> e)
        {
            var clientVersion = e.Request.ProtocolVersion;
            var result = PbVersionCheckResult.Ok;

            if (clientVersion == null)
            {
                result = PbVersionCheckResult.InvalidValue;
            }
            else if (clientVersion < ProtocolVersion)
            {
                result = PbVersionCheckResult.TooLower;
            }
            else if (clientVersion > ProtocolVersion)
            {
                result = PbVersionCheckResult.TooUpper;
            }

            // バージョンチェックの結果を返します。
            e.Response = new PbCheckProtocolVersionResponse(result);
        }

        #region 型名変換
        private static readonly Dictionary<string, string> TypeConvertTable =
            new Dictionary<string, string>()
        {
            {"Ragnarok.Net.ProtoBuf.PbCheckProtocolVersionRequest", "${0}"},
            {"Ragnarok.Net.ProtoBuf.PbCheckProtocolVersionResponse", "${1}"},
            {"Ragnarok.Net.ProtoBuf.PbResponse", "${2}"},
            {"Ragnarok.Net.ProtoBuf.PbAck", "${3}"},
            {"Ragnarok.Net.ProtoBuf.PbNak", "${4}"},
            {"Ragnarok.Net.ProtoBuf", "${5}"},
        };

        /// <summary>
        /// 短縮する型名を登録します。
        /// </summary>
        /// <remarks>
        /// これは送受信双方で同じ設定をする必要があります。
        /// </remarks>
        public static void AddConvertType(string typename)
        {
            if (string.IsNullOrEmpty(typename))
            {
                throw new ArgumentNullException("typename");
            }

            lock (TypeConvertTable)
            {
                TypeConvertTable.Add(
                    typename,
                    string.Format("${{{0}}}", TypeConvertTable.Count));
            }
        }

        /// <summary>
        /// 型名を短くするためのエンコード処理を行います。
        /// </summary>
        public static string EncodeTypeName(string deTypeName)
        {
            if (string.IsNullOrEmpty(deTypeName))
            {
                throw new ArgumentNullException("deTypeName");
            }

            lock (TypeConvertTable)
            {
                return TypeConvertTable.Aggregate(
                    deTypeName,
                    (seed, pair) => seed.Replace(pair.Key, pair.Value));
            }
        }

        /// <summary>
        /// 短縮された型名を元に戻します。
        /// </summary>
        public static string DecodeTypeName(string enTypeName)
        {
            if (string.IsNullOrEmpty(enTypeName))
            {
                throw new ArgumentNullException("enTypeName");
            }

            lock (TypeConvertTable)
            {
                return TypeConvertTable.Aggregate(
                    enTypeName,
                    (seed, pair) => seed.Replace(pair.Value, pair.Key));
            }
        }
        #endregion

        #region receive data
        #region 受信データ解析
        /// <summary>
        /// データの取得後に呼ばれます。
        /// </summary>
        protected override void OnReceived(DataEventArgs e)
        {
            base.OnReceived(e);

            if (e.Error == null)
            {
                var data = new DataSegment<byte>(e.Data, 0, e.DataLength);

                OnReceivedPacket(data);
            }
        }
        
        /// <summary>
        /// 受信パケットの解析を行います。
        /// </summary>
        private void OnReceivedPacket(DataSegment<byte> data)
        {
            // このロックオブジェクトは受信処理を直列的に行うために
            // 使われます。受信処理中に他の受信データを扱うことはできません。
            lock (this.receiveLock)
            {
                while (data.Offset < data.Count)
                {
                    bool parsed = false;

                    try
                    {
                        if (ReceivePacketHeader(data))
                        {
                            if (ReceiveTypename(data))
                            {
                                parsed = ReceivePayload(data);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.ErrorException(ex,
                            "データの受信に失敗しました。");

                        // もしパース中に例外が発生した場合、
                        // (パケットヘッダが正しくない等の場合)
                        // 受信したパケットデータをすべてクリアします。
                        InitReceivedPacket();

                        // 残りのデータの受信を行います。
                        parsed = false;
                    }

                    // データ受信に成功したらそのデータを処理します。
                    if (parsed)
                    {
                        HandleReceivedPacket(
                            this.packetHeader,
                            this.typenameStream.GetBuffer(),
                            this.payloadStream.GetBuffer());

                        InitReceivedPacket();
                    }
                }
            }
        }

        /// <summary>
        /// パケットのヘッダ部分を読み込みます。
        /// </summary>
        private bool ReceivePacketHeader(DataSegment<byte> data)
        {
            // ヘッダーデータが読み込まれていない場合は、それを読み込みます。
            var leaveCount = (int)(
                PbPacketHeader.HeaderLength - this.headerStream.Position);
            if (leaveCount == 0)
            {
                return true;
            }

            // ヘッダーデータの読み込みを行います。
            var length = Math.Min(leaveCount, data.LeaveCount);
            this.headerStream.Write(data.Array, data.Offset, length);
            data.Increment(length);

            // ヘッダー読み込みが終わった後、コンテンツ用の
            // バッファを用意します。
            if (length == leaveCount)
            {
                PacketHeaderReceived();
                return true;
            }

            return false;
        }

        /// <summary>
        /// 型名データを受信します。
        /// </summary>
        private bool ReceiveTypename(DataSegment<byte> data)
        {
            if (this.packetHeader == null)
            {
                return false;
            }

            var leaveCount = (int)(
                this.packetHeader.TypeNameLength - this.typenameStream.Position);
            if (leaveCount == 0)
            {
                return true;
            }

            // 型名部分を読み込みます。
            var length = Math.Min(leaveCount, data.LeaveCount);
            this.typenameStream.Write(data.Array, data.Offset, length);
            data.Increment(length);

            return (length == leaveCount);
        }

        /// <summary>
        /// データ部分を受信します。
        /// </summary>
        private bool ReceivePayload(DataSegment<byte> data)
        {
            if (this.packetHeader == null)
            {
                return false;
            }

            var leaveCount = (int)(
                this.packetHeader.PayloadLength - this.payloadStream.Position);
            if (leaveCount == 0)
            {
                return true;
            }

            // ペイロード部分を読み込みます。
            var length = Math.Min(leaveCount, data.LeaveCount);
            this.payloadStream.Write(data.Array, data.Offset, length);
            data.Increment(length);

            // データを処理したら、受信データ長を知らせ帰ります。
            return (length == leaveCount);
        }

        /// <summary>
        /// 受信データを初期化します。
        /// </summary>
        private void InitReceivedPacket()
        {
            this.headerStream = new MemoryStream(PbPacketHeader.HeaderLength);

            this.packetHeader = null;
            this.typenameStream = null;
            this.payloadStream = null;
        }

        /// <summary>
        /// ヘッダー受信完了後に呼ばれます。
        /// </summary>
        private void PacketHeaderReceived()
        {
            var header = new PbPacketHeader();
            header.SetDecodedHeader(this.headerStream.GetBuffer());

            // 10MB以上のデータはエラーとします。
            if (header.PayloadLength > 10 * 1024 * 1024)
            {
                Disconnect();
                return;
            }

            this.packetHeader = header;
            this.typenameStream = new MemoryStream(header.TypeNameLength);
            this.payloadStream = new MemoryStream(header.PayloadLength);

            Log.Trace(this,
                "Packet Header Received (payload={0}bytes)",
                header.PayloadLength);
        }
        #endregion

        /// <summary>
        /// 受信パケットを処理します。
        /// </summary>
        private void HandleReceivedPacket(PbPacketHeader header,
                                          byte[] typenameBuffer,
                                          byte[] payloadBuffer)
        {
            Type type = null;

            try
            {
                // 型名とメッセージをデシリアライズします。
                type = DeserializeType(typenameBuffer);
                var message = DeserializeMessage(payloadBuffer, type);

                // 対応するハンドラを呼びます。
                if (message is PbAck)
                {
                    HandleAck(header.Id, header.IsResponse, true);
                }
                else if (message is PbNak)
                {
                    HandleAck(header.Id, header.IsResponse, false);
                }
                else
                {
                    // すぐに受信完了を送ります。
                    SendAck(header.Id, header.IsResponse, true);

                    if (!header.IsResponse)
                    {
                        HandleRequestOrCommand(header.Id, message);
                    }
                    else
                    {
                        HandleResponse(header.Id, message);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.ErrorException(this, ex,
                    "データのデシリアライズに失敗しました。" +
                    "(content size={0}, type={1})",
                    (payloadBuffer == null ? -1 : payloadBuffer.Length),
                    type);

                SendAck(header.Id, header.IsResponse, false);
            }
        }

        /// <summary>
        /// 型のフルネームからその型のオブジェクトを取得します。
        /// </summary>
        private Type GetTypeFrom(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return null;
            }

            // キャッシュに登録してあればそれをそのまま返します。
            Type type = null;
            lock (typeCache)
            {
                if (typeCache.TryGetValue(typeName, out type))
                {
                    return type;
                }
            }

            // 型のフルネームからその型を検索し、もしあれば
            // それをキャッシュに登録します。
            type = TypeSerializer.Deserialize(typeName);
            if (type == null)
            {
                return null;
            }

            // キャッシュに登録します。
            lock (typeCache)
            {
                typeCache.Add(typeName, type);
            }

            return type;
        }

        /// <summary>
        /// 型オブジェクトをデシリアライズします。
        /// </summary>
        private Type DeserializeType(byte[] typenameBuffer)
        {
            var typename = Encoding.UTF8.GetString(typenameBuffer);
            if (string.IsNullOrEmpty(typename))
            {
                throw new PbException(
                    "受信した型名が正しくありません。");
            }

            // 短縮された型名を元に戻します。
            typename = DecodeTypeName(typename);

            // デシリアライズする型のオブジェクトを取得します。
            var type = GetTypeFrom(typename);
            if (type == null)
            {
                throw new PbException(
                    string.Format(
                        "{0}: 適切な型が見つかりませんでした。",
                        typename));
            }

            return type;
        }

        /// <summary>
        /// メッセージオブジェクトをデシリアライズします。
        /// </summary>
        private object DeserializeMessage(byte[] payloadBuffer, Type type)
        {
            // サーバーから送られてきたデータを処理します。
            var message = PbUtil.Deserialize(payloadBuffer, type);
            if (message == null)
            {
                throw new PbException(
                    string.Format(
                        "データのデシリアライズに失敗しました。" +
                        "(content size={0}, type={1})",
                        payloadBuffer.Length, type));
            }

            return message;
        }

        /// <summary>
        /// コマンドの応答確認を処理します。
        /// </summary>
        private void HandleAck(int id, bool isResponse, bool success)
        {
            NeedAckInfo needAckInfo;

            Log.Trace(this, "ACKを受信しました。");

            lock (this.needAckDic)
            {
                var dataId = new DataId(id, isResponse); 
                if (!this.needAckDic.TryGetValue(dataId, out needAckInfo))
                {
                    Log.Error(this,
                        "{0}: ACKに対応する送信データがありません。", id);
                    return;
                }

                if (success)
                {
                    // データ送信成功
                    this.needAckDic.Remove(dataId);
                    return;
                }
                else if (needAckInfo.IncrementTryCount() >= 3)
                {
                    // 既定回数以上のデータ送信失敗
                    Log.Error(this,
                        "{0}: 既定回数以上の再送に失敗しました。", id);

                    this.needAckDic.Remove(dataId);
                    return;
                }
            }

            // コマンドの再送処理を行います。
            SendDataInternal(id, isResponse, needAckInfo.SendData, false);
        }

        /// <summary>
        /// リクエストかコマンドを処理します。
        /// </summary>
        private void HandleRequestOrCommand(int id, object message)
        {
            HandlerInfo handlerInfo = null;
            IPbResponse response = null;

            // コマンドを処理するハンドラオブジェクトを取得します。
            lock (this.handlerDic)
            {
                if (!this.handlerDic.TryGetValue(message.GetType(), out handlerInfo))
                {
                    Log.Error(this,
                        "{0}: 適切なハンドラが見つかりませんでした。",
                        message.GetType());
                    return;
                }
            }

            // ログを出力したくない場合もあります。
            if (handlerInfo.IsOutLog)
            {
                Log.Debug(this,
                    "{0}を受信しました。", message.GetType());
            }

            if (handlerInfo.Handler != null)
            {
                try
                {
                    // レスポンスはnullのことがありますが、
                    // それは合法です。
                    response = handlerInfo.Handler(message);
                }
                catch (Exception ex)
                {
                    Log.ErrorException(this, ex,
                        "受信データの処理ハンドラでエラーが発生しました。");

                    response = new PbResponse<PbDummy>()
                    {
                        ErrorCode = PbErrorCode.HandlerException,
                    };
                }
            }

            // もしリクエストなら、レスポンスを返します。
            if (handlerInfo.IsRequestHandler)
            {
                // responseはnullのことがあります。
                SendResponse(id, response);
            }
        }

        /// <summary>
        /// 受信したレスポンスを処理します。
        /// </summary>
        private void HandleResponse(int id, object message)
        {
            var response = message as IPbResponse;
            PbRequestData reqData = null;

            if (response == null)
            {
                throw new InvalidOperationException(
                    "レスポンスの型が正しくありません。");
            }

            // リクエストリストの中から、レスポンスと同じIdを持つ
            // リクエストを探します。
            lock (this.requestDataDic)
            {
                if (!this.requestDataDic.TryGetValue(id, out reqData))
                {
                    Log.Error(this,
                        "サーバーから不正なレスポンスが返されました。" +
                        "(id={0})", id);
                    return;
                }

                this.requestDataDic.Remove(id);
            }

            Log.Debug(this,
                 "{0}を受信しました。", message.GetType());

            // レスポンス処理用のハンドラを呼びます。
            if (reqData != null)
            {
                reqData.OnResponseReceived(response);

                // タイムアウト検出用タイマを殺すために必要です。
                reqData.Dispose();
            }
        }
        #endregion

        #region send data
        /// <summary>
        /// 送信用データのＩＤを取得します。
        /// </summary>
        private int GetNextSendId()
        {
            return Interlocked.Increment(ref this.idCounter);
        }

        /// <summary>
        /// リクエストを出します。
        /// </summary>
        public void SendRequest<TReq, TRes>(TReq request,
                                            PbResponseHandler<TRes> handler,
                                            bool isOutLog = true)
            where TReq : class
        {
            SendRequest(request, DefaultRequestTimeout, handler, isOutLog);
        }

        /// <summary>
        /// タイムアウト付でリクエストを出します。
        /// </summary>
        public void SendRequest<TReq, TRes>(TReq request,
                                            TimeSpan timeout,
                                            PbResponseHandler<TRes> handler,
                                            bool isOutLog = true)
            where TReq: class
        {
            if (request == null)
            {
                return;
            }

            var sendData = new PbSendData(request);
            sendData.Serialize();

            var id = GetNextSendId();
            var reqData = new PbRequestData<TReq, TRes>()
            {
                Id = id,
                Connection = this,
                ResponseReceived = handler,
            };
            reqData.SetTimeout(timeout);

            // 未処理のリクエストとして、リストに追加します。
            lock (this.requestDataDic)
            {
                this.requestDataDic.Add(id, reqData);
            }

            // データを送信します。
            AddNeedAckData(id, false, sendData);
            SendDataInternal(id, false, sendData, isOutLog);
        }

        /// <summary>
        /// コマンドを送ります。
        /// </summary>
        public void SendCommand<TCmd>(TCmd command, bool isOutLog = true)
            where TCmd : class
        {
            if (command == null)
            {
                return;
            }

            var sendData = new PbSendData(command);
            sendData.Serialize();

            // コマンドを送信します。
            var id = GetNextSendId();

            AddNeedAckData(id, false, sendData);
            SendDataInternal(id, false, sendData, isOutLog);
        }

        /// <summary>
        /// レスポンスを送ります。
        /// </summary>
        private void SendResponse(int id, IPbResponse response,
                                  bool isOutLog = true)
        {
            if (response == null)
            {
                return;
            }

            var sendData = new PbSendData(response);
            sendData.Serialize();

            AddNeedAckData(id, true, sendData);
            SendDataInternal(id, true, sendData, isOutLog);
        }

        /// <summary>
        /// コマンドなどの応答確認を送ります。
        /// </summary>
        private void SendAck(int id, bool isResponse, bool success)
        {
            var sendData = (success ? AckData : NakData);

            SendDataInternal(id, isResponse, sendData, true);
        }

        /// <summary>
        /// ACKの受信が必要なデータを登録します。
        /// </summary>
        private void AddNeedAckData(int id, bool isResponse,
                                    PbSendData sendData)
        {
            lock (this.needAckDic)
            {
                var needAck = new NeedAckInfo
                {
                    SendData = sendData,
                };
                var dataId = new DataId(id, isResponse);

                try
                {
                    this.needAckDic.Add(dataId, needAck);
                }
                catch (Exception ex)
                {
                    Log.ErrorException(ex, "");
                }
            }
        }

        /// <summary>
        /// データを送信します。
        /// </summary>
        private void SendDataInternal(int id, bool isResponse,
                                      PbSendData pbSendData, bool isOutLog)
        {
            if (pbSendData == null)
            {
                throw new ArgumentNullException("pbSendData");
            }

            if (pbSendData.SerializedData == null ||
                pbSendData.EncodedTypeName == null)
            {
                throw new PbException("送信データがシリアライズされていません。");
            }

            try
            {
                var typedata = pbSendData.EncodedTypeData;
                var payload = pbSendData.SerializedData;

                // パケットヘッダを用意します。
                var header = new PbPacketHeader
                {
                    Id = id,
                    IsResponse = isResponse,
                    TypeNameLength = typedata.Length,
                    PayloadLength = payload.Length,
                };
                var headerData = header.GetEncodedPacket();

                // 送信データは複数バッファのまま送信します。
                var sendData = new SendData()
                {
                    Socket = this.Socket,
                };
                sendData.AddBuffer(headerData);
                sendData.AddBuffer(typedata);
                sendData.AddBuffer(payload);

                /*if (payload.Length > 2000)
                {
                var filecore = DateTime.Now.ToString("HH:mm:ss.ffff");
                var filename = "tmp/" + sendData.GetType() + filecore + ".dump";
                using (var stream = new FileStream(filename, FileMode.Create))
                {
                    stream.Write(payload, 0, payload.Length);
//                    PbUtil.Deserialize(payload, sendData.GetType());
                }
                }*/

                // データを送信します。
                base.SendData(sendData);

                if (isOutLog)
                {
                    Log.Debug(this,
                        "{0}を送信しました。(content={1}bytes)",
                        pbSendData.TypeName,
                        (payload != null ? payload.Length : -1));
                }
            }
            catch (Exception ex)
            {
                Log.ErrorException(this, ex,
                    "{0}: 送信データのシリアライズに失敗しました。",
                    pbSendData.TypeName);
            }
        }
        #endregion

        /// <summary>
        /// 静的コンストラクタ
        /// </summary>
        static PbConnection()
        {
            AckData = new PbSendData(new PbAck());
            AckData.Serialize();

            NakData = new PbSendData(new PbNak());
            NakData.Serialize();
        }

        /// <summary>
        /// コンストラクタ
        /// </summary>
        public PbConnection()
        {
            InitReceivedPacket();

            ProtocolVersion = new PbProtocolVersion();
            AckTimeout = TimeSpan.FromSeconds(20);
            DefaultRequestTimeout = TimeSpan.MaxValue;

            AddRequestHandler<PbCheckProtocolVersionRequest,
                              PbCheckProtocolVersionResponse>(
                HandleCheckProtocolVersionRequest);
        }
    }
}
