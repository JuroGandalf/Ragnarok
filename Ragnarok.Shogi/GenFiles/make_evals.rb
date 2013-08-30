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
    str = f.read
    
    result = []
    while /\s*\*<analyze>([-]?\d+)\s*(.*)\s*<\/analyze>\s+(\d+)\s+(\S+)\s+/ =~ str
      str = $'
      next if $1 == nil
      
      result << [$4, $1.to_i, $3.to_i]
    end
    result
  end
end

begin
  filepath = "kifu.kif"
  value_list = load_evals_from_kifu(filepath)
  
  fp = File.open("kifu_eval.txt", "w")
  value_list.each_with_index do |a, i|
    fp.printf("M(\"%s%s\", %d),",
      (a[2] % 2 != 0 ? "��" : "��"), a[0], a[1])
    fp.printf(" ") if i % 2 == 0
    fp.printf("\n") if i % 2 == 1
  end
  fp.close
rescue => e
  puts "#{e.message}\n#{e.backtrace}"
end
#gets
