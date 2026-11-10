using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ZedZoneSaveEditor
{
    /// <summary>
    /// 一条 protobuf 字段。
    /// 游戏存档是 protobuf-net 写的，没有加密。这里不需要知道完整结构：
    /// 没动过的字段原样写回（Raw），只有改过的字段才重新编码，所以不认识的数据一个字节都不会变。
    /// </summary>
    internal sealed class PField
    {
        public int Num;
        public int Wire;       // 0 变长整数，1 定长 8 字节，2 带长度（字符串/字节/子结构），5 定长 4 字节
        public ulong Varint;
        public byte[] Data;    // Wire 1/5：原始字节；Wire 2：内容
        public byte[] Raw;     // 整条字段的原始字节（含字段号）；null = 新加的或改过的
        public PMsg Sub;       // Wire 2 被当成子结构解析过时才有

        public bool Changed => Raw == null || (Sub != null && Sub.Changed);
    }

    internal sealed class PMsg
    {
        public readonly List<PField> Fields = new List<PField>();
        private bool _structChanged;

        public bool Changed
        {
            get
            {
                if (_structChanged) return true;
                foreach (var f in Fields) if (f.Changed) return true;
                return false;
            }
        }

        // ---------- 读 ----------

        public static PMsg Parse(byte[] b) => Parse(b, 0, b.Length);

        public static PMsg Parse(byte[] b, int start, int end)
        {
            var m = new PMsg();
            int i = start;
            while (i < end)
            {
                int fieldStart = i;
                ulong key = ReadVarint(b, ref i, end);
                int num = (int)(key >> 3);
                int wire = (int)(key & 7);
                if (num <= 0) throw new FormatException("字段号为 0，数据损坏");
                var f = new PField { Num = num, Wire = wire };
                switch (wire)
                {
                    case 0:
                        f.Varint = ReadVarint(b, ref i, end);
                        break;
                    case 1:
                        if (i + 8 > end) throw new FormatException("数据被截断");
                        f.Data = Slice(b, i, 8); i += 8;
                        break;
                    case 5:
                        if (i + 4 > end) throw new FormatException("数据被截断");
                        f.Data = Slice(b, i, 4); i += 4;
                        break;
                    case 2:
                        ulong len = ReadVarint(b, ref i, end);
                        if (len > (ulong)(end - i)) throw new FormatException("长度超出范围，数据损坏");
                        f.Data = Slice(b, i, (int)len); i += (int)len;
                        break;
                    default:
                        throw new FormatException("不支持的字段类型 " + wire);
                }
                f.Raw = Slice(b, fieldStart, i - fieldStart);
                m.Fields.Add(f);
            }
            if (i != end) throw new FormatException("结尾对不上");
            return m;
        }

        private static byte[] Slice(byte[] b, int start, int len)
        {
            var r = new byte[len];
            Buffer.BlockCopy(b, start, r, 0, len);
            return r;
        }

        private static ulong ReadVarint(byte[] b, ref int i, int end)
        {
            ulong r = 0;
            int shift = 0;
            while (true)
            {
                if (i >= end) throw new FormatException("数据被截断");
                byte c = b[i++];
                r |= (ulong)(c & 0x7F) << shift;
                if (c < 0x80) return r;
                shift += 7;
                if (shift > 63) throw new FormatException("整数太长，数据损坏");
            }
        }

        // ---------- 写 ----------

        public byte[] ToBytes()
        {
            var ms = new MemoryStream();
            Write(ms);
            return ms.ToArray();
        }

        private void Write(Stream s)
        {
            foreach (var f in Fields)
            {
                if (!f.Changed)
                {
                    s.Write(f.Raw, 0, f.Raw.Length);
                    continue;
                }
                WriteVarint(s, ((ulong)f.Num << 3) | (uint)f.Wire);
                switch (f.Wire)
                {
                    case 0:
                        WriteVarint(s, f.Varint);
                        break;
                    case 1:
                    case 5:
                        s.Write(f.Data, 0, f.Data.Length);
                        break;
                    case 2:
                        byte[] content = f.Sub != null ? f.Sub.ToBytes() : f.Data;
                        WriteVarint(s, (ulong)content.Length);
                        s.Write(content, 0, content.Length);
                        break;
                }
            }
        }

        private static void WriteVarint(Stream s, ulong v)
        {
            while (v >= 0x80)
            {
                s.WriteByte((byte)(v | 0x80));
                v >>= 7;
            }
            s.WriteByte((byte)v);
        }

        // ---------- 查找 ----------

        public PField First(int num)
        {
            foreach (var f in Fields) if (f.Num == num) return f;
            return null;
        }

        public List<PField> All(int num)
        {
            var r = new List<PField>();
            foreach (var f in Fields) if (f.Num == num) r.Add(f);
            return r;
        }

        public bool Has(int num) => First(num) != null;

        public void RemoveAll(int num)
        {
            if (Fields.RemoveAll(f => f.Num == num) > 0) _structChanged = true;
        }

        /// <summary>同一个非重复字段出现多次时，protobuf 取最后一个；我们改值时只留一个。</summary>
        private PField Single(int num, int wire)
        {
            PField keep = null;
            for (int i = Fields.Count - 1; i >= 0; i--)
            {
                if (Fields[i].Num != num) continue;
                if (keep == null) keep = Fields[i];
                else { Fields.RemoveAt(i); _structChanged = true; }
            }
            if (keep == null)
            {
                keep = new PField { Num = num, Wire = wire };
                InsertSorted(keep);
            }
            keep.Wire = wire;
            keep.Sub = null;
            keep.Raw = null;
            return keep;
        }

        private PField Last(int num)
        {
            for (int i = Fields.Count - 1; i >= 0; i--) if (Fields[i].Num == num) return Fields[i];
            return null;
        }

        // ---------- 取值 / 设值（字段不存在 = 游戏里的默认值 0） ----------

        public float GetFloat(int num, float def = 0f)
        {
            var f = Last(num);
            if (f == null) return def;
            if (f.Wire == 5) return BitConverter.ToSingle(f.Data, 0);
            if (f.Wire == 1) return (float)BitConverter.ToDouble(f.Data, 0);
            return def;
        }

        public void SetFloat(int num, float v)
        {
            var f = Single(num, 5);
            f.Data = BitConverter.GetBytes(v);
        }

        public long GetLong(int num, long def = 0)
        {
            var f = Last(num);
            if (f == null || f.Wire != 0) return def;
            return unchecked((long)f.Varint);
        }

        public int GetInt(int num, int def = 0) => unchecked((int)GetLong(num, def));

        public bool GetBool(int num) => GetLong(num) != 0;

        /// <summary>protobuf-net 写 int 时负数按 64 位补码写（10 字节），这里一样。</summary>
        public void SetLong(int num, long v)
        {
            var f = Single(num, 0);
            f.Varint = unchecked((ulong)v);
        }

        public void SetInt(int num, int v) => SetLong(num, v);

        public void SetBool(int num, bool v) => SetLong(num, v ? 1 : 0);

        public string GetString(int num)
        {
            var f = Last(num);
            if (f == null || f.Wire != 2) return null;
            return Encoding.UTF8.GetString(f.Data);
        }

        public void SetString(int num, string v)
        {
            var f = Single(num, 2);
            f.Data = Encoding.UTF8.GetBytes(v ?? "");
        }

        /// <summary>把某个字段当成子结构来读写。没有就返回 null。</summary>
        public PMsg GetMsg(int num)
        {
            var f = Last(num);
            return f == null ? null : AsMsg(f);
        }

        public PMsg GetOrAddMsg(int num)
        {
            var m = GetMsg(num);
            if (m != null) return m;
            var f = new PField { Num = num, Wire = 2, Data = new byte[0], Sub = new PMsg() };
            f.Sub._structChanged = true;
            InsertSorted(f);
            return f.Sub;
        }

        /// <summary>新加的字段按字段号插到合适位置（protobuf-net 自己写的时候也是按字段号排的）。</summary>
        private void InsertSorted(PField f)
        {
            int at = Fields.FindIndex(x => x.Num > f.Num);
            if (at < 0) Fields.Add(f); else Fields.Insert(at, f);
            _structChanged = true;
        }

        /// <summary>在重复字段（比如物品列表）里追加一个子结构。</summary>
        public void AddMsg(int num, PMsg sub)
        {
            sub._structChanged = true;
            InsertSorted(new PField { Num = num, Wire = 2, Data = new byte[0], Sub = sub });
        }

        /// <summary>从重复字段里删掉某一个子结构（按对象找）。</summary>
        public bool RemoveMsg(PMsg sub)
        {
            int i = Fields.FindIndex(x => x.Sub == sub);
            if (i < 0) return false;
            Fields.RemoveAt(i);
            _structChanged = true;
            return true;
        }

        /// <summary>把某个子结构原地换成另一个（重复字段里位置不变，比如武器栏第 2 格）。</summary>
        public bool ReplaceMsg(PMsg old, PMsg neu)
        {
            var f = Fields.Find(x => x.Sub == old);
            if (f == null) return false;
            f.Sub = neu;
            f.Data = new byte[0];
            f.Raw = null;
            neu._structChanged = true;
            _structChanged = true;
            return true;
        }

        /// <summary>非重复的子结构字段整个换成 sub（没有就按字段号插进去）。</summary>
        public void SetMsg(int num, PMsg sub)
        {
            var f = Single(num, 2);
            f.Data = new byte[0];
            f.Sub = sub;
            sub._structChanged = true;
        }

        /// <summary>整个子结构换成现成的字节（比如把身上背包的内容同步到另一处）。</summary>
        public void SetMsgBytes(int num, byte[] data)
        {
            var f = Single(num, 2);
            f.Data = data;
        }

        public byte[] GetBytes(int num)
        {
            var f = Last(num);
            if (f == null || f.Wire != 2) return null;
            return f.Sub != null ? f.Sub.ToBytes() : f.Data;
        }

        public PMsg Clone() => Parse(ToBytes());

        public void MarkChanged() => _structChanged = true;

        /// <summary>float 数组（protobuf-net 默认不打包：每个数一条 4 字节字段；也兼容打包写法）。</summary>
        public List<float> GetFloatList(int num)
        {
            var r = new List<float>();
            foreach (var f in Fields)
            {
                if (f.Num != num) continue;
                if (f.Wire == 5) r.Add(BitConverter.ToSingle(f.Data, 0));
                else if (f.Wire == 2)
                    for (int i = 0; i + 4 <= f.Data.Length; i += 4) r.Add(BitConverter.ToSingle(f.Data, i));
            }
            return r;
        }

        public void SetFloatList(int num, IList<float> values)
        {
            int at = ReplaceAt(num);
            foreach (var v in values)
                Fields.Insert(at++, new PField { Num = num, Wire = 5, Data = BitConverter.GetBytes(v) });
        }

        public void SetLongList(int num, IList<long> values)
        {
            int at = ReplaceAt(num);
            foreach (var v in values)
                Fields.Insert(at++, new PField { Num = num, Wire = 0, Varint = unchecked((ulong)v) });
        }

        /// <summary>删掉某字段的所有条目，返回新值该插入的位置（原来第一条的位置，没有就按字段号排）。</summary>
        private int ReplaceAt(int num)
        {
            int at = Fields.FindIndex(x => x.Num == num);
            if (at >= 0) Fields.RemoveAll(x => x.Num == num);
            else at = Fields.FindIndex(x => x.Num > num);
            if (at < 0) at = Fields.Count;
            _structChanged = true;
            return at;
        }

        public List<PMsg> GetMsgs(int num)
        {
            var r = new List<PMsg>();
            foreach (var f in Fields) if (f.Num == num) r.Add(AsMsg(f));
            return r;
        }

        private static PMsg AsMsg(PField f)
        {
            if (f.Wire != 2) throw new FormatException($"字段 {f.Num} 不是子结构");
            if (f.Sub == null) f.Sub = Parse(f.Data);
            return f.Sub;
        }

        public List<long> GetLongList(int num)
        {
            var r = new List<long>();
            foreach (var f in Fields)
            {
                if (f.Num != num) continue;
                if (f.Wire == 0) r.Add(unchecked((long)f.Varint));
                else if (f.Wire == 2)
                {
                    // 打包写法（packed）
                    int i = 0;
                    while (i < f.Data.Length) r.Add(unchecked((long)ReadVarint(f.Data, ref i, f.Data.Length)));
                }
            }
            return r;
        }
    }
}
