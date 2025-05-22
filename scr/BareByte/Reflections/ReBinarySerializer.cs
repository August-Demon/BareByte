using BareByte.Attributes;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace BareByte.Reflections
{
    public static class ReBinarySerializer
    {
        // int = int32 = 4 byte
        // uint = uint32 = 4 byte
        // float = float32 = 4 byte
        // double = float64 = 8 byte
        // decimal = 128 bit = 16 byte
        // short = int16 = 2 byte
        // ushort = uint16 = 2 byte
        // long = int64 = 8 byte
        // ulong = uint64 = 8 byte
        // byte = uint8 = 1 byte
        // sbyte = int8 = 1 byte
        // bool = 1 byte
        // char = 2 byte
        // string = 2 byte + n byte (n = string length)

        // 问题1 反序列化无法处理多个集合类型
        // 问题2 反序列化无法处理集合类型的集合类型  

        // 解决方案
        // 1. 写入前使用Queue队列来保存长度
        // 2. 读取时使用Queue队列来获取长度
        // 3. 这样可以解决写入长度导致byte[]长度不一致的问题


        private static readonly Queue<int> CollectionLengthStack = new Queue<int>();

        private static readonly Dictionary<Type, Action<BinaryWriter, object>> TypeWriteReadAction = new Dictionary<Type, Action<BinaryWriter, object>>
        {
                { typeof(int),      (w, v) => w.Write((int)v) },
                { typeof(uint),     (w, v) => w.Write((uint)v) },
                { typeof(float),    (w, v) => w.Write((float)v) },
                { typeof(double),   (w, v) => w.Write((double)v) },
                { typeof(decimal),  (w, v) => w.Write((decimal)v) },
                { typeof(short),    (w, v) => w.Write((short)v) },
                { typeof(ushort),   (w, v) => w.Write((ushort)v) },
                { typeof(long),     (w, v) => w.Write((long)v) },
                { typeof(ulong),    (w, v) => w.Write((ulong)v) },
                { typeof(byte),     (w, v) => w.Write((byte)v) },
                { typeof(sbyte),    (w, v) => w.Write((sbyte)v) },
                { typeof(bool),     (w, v) => w.Write((bool)v) },
                { typeof(char),     (w, v) => w.Write((char)v) },
                { typeof(string),   (w, v) => w.Write((string)v) }
        };

        public static byte[] Serialize<T>(T obj) where T : new()
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));
            var type = typeof(T);
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                WriteValue(writer, type, obj);
                return ms.ToArray();
            }
        }


        // 对象反序列化包含嵌套对象 集合
        public static T Deserialize<T>(byte[] data) where T : new()
        {
            if (data == null || data.Length == 0) throw new ArgumentNullException(nameof(data));
            var type = typeof(T);
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                return (T)ReadValue(reader, type);
            }
        }

        // 获取可序列化属性
        private static IEnumerable<PropertyInfo> GetSerializableProperties(Type type)
        {
            return type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.CanRead && p.CanWrite &&
                p.GetIndexParameters().Length == 0 &&
                !p.IsDefined(typeof(IgnoredMemberAttribute), true));
        }




        private static void WriteValue<T>(BinaryWriter writer, Type type, T value)
        {
            if (TypeWriteReadAction.TryGetValue(type, out var writeAction))
            {
                writeAction(writer, value);
            }
            else if (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string))
            {
                var enumerable = (IEnumerable)value;
                var elementType = type.IsArray ? type.GetElementType() :
                    type.IsGenericType ? type.GetGenericArguments()[0] : typeof(object);

                var count = enumerable.Cast<object>().Count();
                CollectionLengthStack.Enqueue(count);


                foreach (var item in (IEnumerable)value)
                {
                    WriteValue(writer, elementType, item);
                }
            }
            else if (!type.IsPrimitive && !type.IsEnum && type != typeof(string))
            {
                foreach (var prop in GetSerializableProperties(type))
                {
                    var v = prop.GetValue(value);
                    WriteValue(writer, prop.PropertyType, v);
                }
            }
        }

        private static object ReadValue(BinaryReader reader, Type type)
        {
            if (TypeWriteReadAction.ContainsKey(type))
            {
                switch (Type.GetTypeCode(type))
                {
                    case TypeCode.Int32: return reader.ReadInt32();
                    case TypeCode.UInt32: return reader.ReadUInt32();
                    case TypeCode.Single: return reader.ReadSingle();
                    case TypeCode.Double: return reader.ReadDouble();
                    case TypeCode.Decimal: return reader.ReadDecimal();
                    case TypeCode.Int16: return reader.ReadInt16();
                    case TypeCode.UInt16: return reader.ReadUInt16();
                    case TypeCode.Int64: return reader.ReadInt64();
                    case TypeCode.UInt64: return reader.ReadUInt64();
                    case TypeCode.Byte: return reader.ReadByte();
                    case TypeCode.SByte: return reader.ReadSByte();
                    case TypeCode.Boolean: return reader.ReadBoolean();
                    case TypeCode.Char: return reader.ReadChar();
                    case TypeCode.String: return reader.ReadString();
                    default: throw new NotSupportedException($"不支持的基础类型: {type}");
                }
            }
            if (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string))
            {
                var elementType = type.IsArray ? type.GetElementType() :
                    type.IsGenericType ? type.GetGenericArguments()[0] : typeof(object);

                //int count = GetCollectionLength(type);
                int count;
                if (CollectionLengthStack.Count <= 0)
                {
                    count = 0;
                }
                else
                {
                    count = CollectionLengthStack.Dequeue();
                }



                var listType = typeof(List<>).MakeGenericType(elementType);
                var list = (IList)Activator.CreateInstance(listType);

                // 读取集合元素
                for (int i = 0; i < count; i++)
                {
                    var item = ReadValue(reader, elementType);
                    list.Add(item);
                }

                // 这里假设集合长度未知，需由外部协议保证读取正确数量，否则可能导致数据错位
                //while (reader.BaseStream.Position < reader.BaseStream.Length)
                //{
                //    var item = ReadValue(reader, elementType);
                //    list.Add(item);
                //}
                // 如果原类型是数组，转换为数组返回
                if (type.IsArray)
                {
                    var array = Array.CreateInstance(elementType, list.Count);
                    list.CopyTo(array, 0);
                    return array;
                }
                return list;
            }
            if (!type.IsPrimitive && !type.IsEnum && type != typeof(string))
            {
                var obj = Activator.CreateInstance(type);
                foreach (var prop in GetSerializableProperties(type))
                {
                    var v = ReadValue(reader, prop.PropertyType);
                    prop.SetValue(obj, v);
                }
                return obj;
            }
            throw new NotSupportedException($"不支持的类型: {type}");
        }
    }
}
