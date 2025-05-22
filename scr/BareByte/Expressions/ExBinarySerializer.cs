using BareByte.Attributes;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace BareByte.Expressions
{
    public static class ExBinarySerializer<T>
    {
        private static readonly Action<BinaryWriter, T> _writer;
        private static readonly Func<BinaryReader, T> _reader;

        private static readonly Queue<int> CollectionLengthQueue;

        static ExBinarySerializer()
        {
            CollectionLengthQueue = new Queue<int>();

            var writerParam = Expression.Parameter(typeof(BinaryWriter), "writer");
            var objParam = Expression.Parameter(typeof(T), "obj");
            var writeBody = BuildWriteExpression(writerParam, objParam, typeof(T));
            _writer = Expression.Lambda<Action<BinaryWriter, T>>(writeBody, writerParam, objParam).Compile();

            var readerParam = Expression.Parameter(typeof(BinaryReader), "reader");
            var readBody = BuildReadExpression(readerParam, typeof(T));
            _reader = Expression.Lambda<Func<BinaryReader, T>>(readBody, readerParam).Compile();
        }

        public static byte[] Serialize(T obj)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                _writer(writer, obj);
                return ms.ToArray();
            }
        }

        public static T Deserialize(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return _reader(reader);
            }
        }


        private static Expression BuildWriteExpression(ParameterExpression writer, Expression obj, Type type)
        {
            if (type == typeof(string))
            {
                return Expression.Call(writer, typeof(BinaryWriter).GetMethod("Write", new[] { typeof(string) }), obj);
            }
            if (type.IsPrimitive)
            {
                var method = typeof(BinaryWriter).GetMethod("Write", new[] { type });
                return Expression.Call(writer, method, obj);
            }
            if (type.IsEnum)
            {
                var underlying = Enum.GetUnderlyingType(type);
                var method = typeof(BinaryWriter).GetMethod("Write", new[] { underlying });
                return Expression.Call(writer, method, Expression.Convert(obj, underlying));
            }

            if (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string))
            {
                Type elementType = typeof(object);

                if (type.IsArray)
                    elementType = type.GetElementType();
                else if (type.IsGenericType)
                    elementType = type.GetGenericArguments()[0];

                var countProp = type.GetProperty("Count");
                Expression countExpr = countProp != null
                    ? (Expression)Expression.Property(obj, countProp)
                    : Expression.Constant(0);

                // 将集合长度存入队列
                var enqueueMethod = typeof(Queue<int>).GetMethod("Enqueue");
                var enqueueExpr = Expression.Call(Expression.Constant(CollectionLengthQueue), enqueueMethod, countExpr);

                var loopVar = Expression.Variable(elementType, "item");
                var enumerable = Expression.Variable(typeof(IEnumerable), "enumerable");
                var assignEnum = Expression.Assign(enumerable, Expression.Convert(obj, typeof(IEnumerable)));
                var getEnumerator = typeof(IEnumerable).GetMethod("GetEnumerator");
                var enumerator = Expression.Variable(typeof(IEnumerator), "enumerator");
                var assignEnumerator = Expression.Assign(enumerator, Expression.Call(enumerable, getEnumerator));
                var moveNext = typeof(IEnumerator).GetMethod("MoveNext");
                var current = typeof(IEnumerator).GetProperty("Current");

                var breakLabel = Expression.Label("LoopBreak");

                var loop = Expression.Loop(
                    Expression.IfThenElse(
                        Expression.Call(enumerator, moveNext),
                        Expression.Block(
                            new[] { loopVar },
                            Expression.Assign(loopVar, Expression.Convert(Expression.Property(enumerator, current), elementType)),
                            BuildWriteExpression(writer, loopVar, elementType)
                        ),
                        Expression.Break(breakLabel)
                    ),
                    breakLabel
                );

                return Expression.Block(
                    new[] { enumerable, enumerator },
                    enqueueExpr, // 先存入长度
                    assignEnum,
                    assignEnumerator,
                    loop
                );
            }

            var exprs = new List<Expression>();
            foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && !p.IsDefined(typeof(IgnoredMemberAttribute), true)))
            {
                var memberAccess = Expression.Property(obj, prop);
                exprs.Add(BuildWriteExpression(writer, memberAccess, prop.PropertyType));
            }
            if (exprs.Count == 0)
                throw new InvalidOperationException($"类型 {type} 没有可序列化的属性。");
            return Expression.Block(exprs);
        }

        private static Expression BuildReadExpression(ParameterExpression reader, Type type)
        {
            if (type == typeof(string))
            {
                return Expression.Call(reader, typeof(BinaryReader).GetMethod("ReadString"));
            }
            if (type.IsPrimitive)
            {
                var method = typeof(BinaryReader).GetMethod("Read" + FirstCharToUpper(type.Name));
                return Expression.Call(reader, method);
            }
            if (type.IsEnum)
            {
                var underlying = Enum.GetUnderlyingType(type);
                var method = typeof(BinaryReader).GetMethod("Read" + FirstCharToUpper(underlying.Name));
                return Expression.Convert(Expression.Call(reader, method), type);
            }
            if (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string))
            {
                Type elementType = typeof(object);
                if (type.IsArray)
                    elementType = type.GetElementType();
                else if (type.IsGenericType)
                    elementType = type.GetGenericArguments()[0];

                // 从队列中获取集合长度
                var dequeueMethod = typeof(Queue<int>).GetMethod("Dequeue");
                var countVar = Expression.Variable(typeof(int), "count");
                var assignCount = Expression.Assign(countVar, Expression.Call(Expression.Constant(CollectionLengthQueue), dequeueMethod));

                // 创建集合
                var listType = typeof(List<>).MakeGenericType(elementType);
                var listVar = Expression.Variable(listType, "list");
                var assignList = Expression.Assign(listVar, Expression.New(listType));

                // 循环读取集合元素
                var loopVar = Expression.Variable(elementType, "item");
                var loopBreak = Expression.Label("LoopBreak");

                var loop = Expression.Loop(
                    Expression.IfThenElse(
                        Expression.GreaterThan(countVar, Expression.Constant(0)),
                        Expression.Block(
                            new[] { loopVar },
                            Expression.Assign(loopVar, BuildReadExpression(reader, elementType)),
                            Expression.Call(listVar, listType.GetMethod("Add"), loopVar),
                            Expression.PostDecrementAssign(countVar) // 减少计数
                        ),
                        Expression.Break(loopBreak)
                    ),
                    loopBreak
                );

                // 返回集合
                Expression result = listVar;
                if (type.IsArray)
                {
                    var toArray = listType.GetMethod("ToArray");
                    result = Expression.Call(listVar, toArray);
                }

                return Expression.Block(
                    new[] { countVar, listVar },
                    assignCount,
                    assignList,
                    loop,
                    result
                );


            }

            var objVar = Expression.Variable(type, "obj");
            var assignObj = Expression.Assign(objVar, Expression.New(type));
            var exprs = new List<Expression> { assignObj };
            foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && !p.IsDefined(typeof(IgnoredMemberAttribute), true)))
            {
                var value = BuildReadExpression(reader, prop.PropertyType);
                exprs.Add(Expression.Assign(Expression.Property(objVar, prop), value));
            }
            exprs.Add(objVar);
            return Expression.Block(new[] { objVar }, exprs);
        }

        private static string FirstCharToUpper(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            if (input == "Boolean") return "Boolean";
            if (input == "SByte") return "SByte";
            if (input == "Byte") return "Byte";
            if (input == "Int16") return "Int16";
            if (input == "UInt16") return "UInt16";
            if (input == "Int32") return "Int32";
            if (input == "UInt32") return "UInt32";
            if (input == "Int64") return "Int64";
            if (input == "UInt64") return "UInt64";
            if (input == "Single") return "Single";
            if (input == "Double") return "Double";
            if (input == "Char") return "Char";
            if (input == "Decimal") return "Decimal";
            return char.ToUpper(input[0]) + input.Substring(1);
        }
    }
}
