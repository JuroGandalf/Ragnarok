using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.ComponentModel;
using System.Runtime.Serialization;

using Ragnarok;
using Ragnarok.ObjectModel;

namespace Ragnarok.Shogi
{
    /// <summary>
    /// �Ֆʏ�̋�������܂��B
    /// </summary>
    /// <remarks>
    /// �Ֆʏ�̋��\������ɂ͋�̏��{��オ�K�v�ł��B
    /// ���̃N���X��<see cref="Piece"/>�N���X�ɐ��̃v���p�e�B��
    /// �ǉ����������̂��̂ł��B
    /// </remarks>
    [DataContract()]
    public class BoardPiece : IEquatable<BoardPiece>
    {
        /// <summary>
        /// ���̋���̋���擾�܂��͐ݒ肵�܂��B
        /// </summary>
        public BWType BWType
        {
            get;
            set;
        }

        /// <summary>
        /// ��̎�ނ��擾�܂��͐ݒ肵�܂��B
        /// </summary>
        public PieceType PieceType
        {
            get;
            set;
        }

        /// <summary>
        /// ������Ă邩�ǂ������擾�܂��͐ݒ肵�܂��B
        /// </summary>
        public bool IsPromoted
        {
            get;
            set;
        }

        /// <summary>
        /// �����Ȃ��̋�I�u�W�F�N�g���擾���܂��B
        /// </summary>
        public Piece Piece
        {
            get { return new Piece(PieceType, IsPromoted); }
        }

        /// <summary>
        /// �����Ȃ��̋�I�u�W�F�N�g���擾���܂��B(null�Ή���)
        /// </summary>
        public static Piece GetPiece(BoardPiece self)
        {
            return (self != null ?
                new Piece(self.PieceType, self.IsPromoted) :
                null);
        }

        /// <summary>
        /// �I�u�W�F�N�g�̃R�s�[���쐬���܂��B
        /// </summary>
        public BoardPiece Clone()
        {
            return new BoardPiece(PieceType, IsPromoted, BWType);
        }

        /// <summary>
        /// ��𕶎��񉻂��܂��B
        /// </summary>
        public override string ToString()
        {
            return Stringizer.ToString(Piece);
        }

        /// <summary>
        /// �I�u�W�F�N�g�̑Ó��������؂��܂��B
        /// </summary>
        public bool Validate()
        {
            if (!EnumEx.IsDefined(BWType))
            {
                return false;
            }

            if (!Piece.Validate())
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// �I�u�W�F�N�g�̓��l���𔻒肵�܂��B
        /// </summary>
        public override bool Equals(object obj)
        {
            var result = this.PreEquals(obj);
            if (result.HasValue)
            {
                return result.Value;
            }

            return Equals(obj as BoardPiece);
        }

        /// <summary>
        /// �I�u�W�F�N�g�̓��l���𔻒肵�܂��B
        /// </summary>
        public bool Equals(BoardPiece other)
        {
            if ((object)other == null)
            {
                return false;
            }

            if (BWType != other.BWType)
            {
                return false;
            }

            if (PieceType != other.PieceType)
            {
                return false;
            }

            if (IsPromoted != other.IsPromoted)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// �n�b�V���l��Ԃ��܂��B
        /// </summary>
        public override int GetHashCode()
        {
            return (
                BWType.GetHashCode() ^
                PieceType.GetHashCode() ^
                IsPromoted.GetHashCode());
        }

        /// <summary>
        /// �I�u�W�F�N�g���r���܂��B
        /// </summary>
        public static bool operator ==(BoardPiece x, BoardPiece y)
        {
            return Util.GenericEquals(x, y);
        }

        /// <summary>
        /// �I�u�W�F�N�g���r���܂��B
        /// </summary>
        public static bool operator !=(BoardPiece x, BoardPiece y)
        {
            return !(x == y);
        }

        #region �V���A���C�Y
        [DataMember(Order = 1, IsRequired = true)]
        private byte serializeBits = 0;

        /// <summary>
        /// �V���A���C�Y���s���܂��B
        /// </summary>
        [CLSCompliant(false)]
        public byte Serialize()
        {
            byte bits = 0;

            // 2bits
            bits |= (byte)BWType;
            // 4bits
            bits |= (byte)((uint)PieceType << 2);
            // 1bits
            bits |= (byte)((uint)(IsPromoted ? 1 : 0) << 6);

            return bits;
        }

        /// <summary>
        /// �f�V���A���C�Y���s���܂��B
        /// </summary>
        [CLSCompliant(false)]
        public void Deserialize(uint bits)
        {
            BWType = (BWType)((bits >> 0) & 0x03);
            PieceType = (PieceType)((bits >> 2) & 0x0f);
            IsPromoted = (((bits >> 6) & 0x01) != 0);
        }

        /// <summary>
        /// �V���A���C�Y�O�ɌĂ΂�܂��B
        /// </summary>
        [OnSerializing()]
        private void BeforeSerialize(StreamingContext context)
        {
            this.serializeBits = Serialize();
        }

        /// <summary>
        /// �f�V���A���C�Y��ɌĂ΂�܂��B
        /// </summary>
        [OnDeserialized()]
        private void AfterDeserialize(StreamingContext context)
        {
            Deserialize(this.serializeBits);
        }
        #endregion

        /// <summary>
        /// �R���X�g���N�^
        /// </summary>
        public BoardPiece(PieceType pieceType, bool isPromoted, BWType bwType)
        {
            BWType = bwType;
            PieceType = pieceType;
            IsPromoted = isPromoted;
        }

        /// <summary>
        /// �R���X�g���N�^
        /// </summary>
        public BoardPiece(PieceType pieceType, BWType bwType)
            : this(pieceType, false, bwType)
        {
        }

        /// <summary>
        /// �R���X�g���N�^
        /// </summary>
        public BoardPiece(Piece piece, BWType bwType)
            : this(piece.PieceType, piece.IsPromoted, bwType)
        {
        }

        /// <summary>
        /// �R���X�g���N�^
        /// </summary>
        public BoardPiece()
        {
        }
    }
}
