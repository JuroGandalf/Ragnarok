#!/usr/bin/ruby -Ks
#
# ���w�̕]���l�t�������t�@�C������]���l�݂̂����o���܂��B
#

require 'kconv'

#
# �����t�@�C������]���l�݂̂����o���܂��B
#
def load_evals_from_kifu(filepath)
  File.open(filepath) do |f|
    f.map {|line| /\s*\*<analyze>([-]?\d+)\s*(.*)\s*<\/analyze>/ =~ line; $1}.
    select {|str| str != nil}.
    map {|str| str.to_i}
  end
end

# 
filepath = "kifu.kif"
value_list = load_evals_from_kifu(filepath)

fp = File.open("kifu_eval.txt", "w")
value_list.each_with_index do |v, i|
  fp.printf("% 7d,", v)
  fp.printf("\n") if i % 5 == 4
end
fp.close
