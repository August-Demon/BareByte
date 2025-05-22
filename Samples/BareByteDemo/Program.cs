using BareByte.Expressions;
using BareByte.Reflections;
using System;
using System.Collections.Generic;

namespace BareByteDemo
{
    internal class Program
    {
        static void Main(string[] args)
        {
            // 测试用反射 和表达式序列化 1000个的处理数度
            var list = new List<ErrorLog>();
            for (int i = 0; i < 10; i++)
            {
                list.Add(new ErrorLog
                {
                    Id = i,
                    Context = i + 1,
                    Stack = $"{i + 1}",
                    Sub = new SubLog { Secret = i + 1, SubId = i + 1 },
                    SubLogs = new List<SubLog> { new SubLog { SubId = i + 1, Secret = i + 1 } }
                });
            }

            // 确保引用正确的命名空间和类
            var watch = System.Diagnostics.Stopwatch.StartNew();
            watch.Start();
            var r = ExBinarySerializer<List<ErrorLog>>.Serialize(list); 
            var o = ExBinarySerializer<List<ErrorLog>>.Deserialize(r);
            watch.Stop();
            Console.WriteLine($"反射序列化耗时: {watch.ElapsedMilliseconds}ms");

            var watch2 = System.Diagnostics.Stopwatch.StartNew();
            watch2.Start();
            var r2 = ReBinarySerializer.Serialize(list);
            var o2 = ReBinarySerializer.Deserialize<List<ErrorLog>>(r2);
            watch2.Stop();
            Console.WriteLine($"表达式序列化耗时: {watch2.ElapsedMilliseconds}ms");

            Console.ReadKey();
        }
    }

    public class SubLog
    {
        public int SubId { get; set; }

        public double Secret { get; set; }
    }

    public class ErrorLog
    {
        public float Id { get; set; }

        public float Context { get; set; }

        public string Stack { get; set; }

        public SubLog Sub { get; set; }

        public List<SubLog> SubLogs { get; set; }
    }
}
