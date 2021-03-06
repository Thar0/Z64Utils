﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using N64;
using RDP;
using Syroot.BinaryData;
using Common;

namespace Z64
{

    [Serializable]
    public class Z64ObjectException : Exception
    {
        public Z64ObjectException() { }
        public Z64ObjectException(string message) : base(message) { }
        public Z64ObjectException(string message, Exception inner) : base(message, inner) { }
        protected Z64ObjectException(
          System.Runtime.Serialization.SerializationInfo info,
          System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }
    
    public class Z64Object
    {
        public enum EntryType
        {
            DList,
            Vertex,
            Texture,
            Unknown,
        }

        public abstract class ObjectHolder
        {
            public string Name { get; set; }

            protected ObjectHolder(string name)
            {
                if (string.IsNullOrEmpty(name))
                    throw new ArgumentException("Invalid Name", nameof(name));
                Name = name;
            }

            public abstract EntryType GetEntryType();
            public abstract byte[] GetData();
            public abstract void SetData(byte[] data);
            public abstract int GetSize();

            public override string ToString() => $"{Name} ({GetEntryType()})";
        }
        public class DListHolder : ObjectHolder
        {
            public byte[] UCode { get; set; }

            public DListHolder(string name, byte[] ucode) : base(name) => UCode = ucode;

            public override EntryType GetEntryType() => EntryType.DList;
            public override byte[] GetData() => UCode;
            public override void SetData(byte[] data) => UCode = data;
            public override int GetSize() => UCode.Length;
        }
        public class VertexHolder : ObjectHolder
        {
            public List<RDPVtx> Vertices { get; set; }

            public VertexHolder(string name, List<RDPVtx> vtx) : base(name) => Vertices = vtx;

            public override EntryType GetEntryType() => EntryType.Vertex;
            public override void SetData(byte[] data)
            {
                if (data.Length % 0x10 != 0)
                    throw new Z64ObjectException($"Invalid size for a vertex buffer (0x{data.Length:X})");

                int count = data.Length / 0x10;

                Vertices = new List<RDPVtx>();
                using (MemoryStream ms = new MemoryStream(data))
                {
                    BinaryStream br = new BinaryStream(ms);
                    br.ByteConverter = ByteConverter.Big;

                    for (int i = 0; i < count; i++)
                        Vertices.Add(new RDPVtx(br));
                }
            }
            public override byte[] GetData()
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    BinaryStream bw = new BinaryStream(ms);
                    bw.ByteConverter = ByteConverter.Big;

                    for (int i = 0; i < Vertices.Count; i++)
                        Vertices[i].Write(bw);

                    return ms.GetBuffer().Take((int)ms.Length).ToArray();
                }
            }
            public override int GetSize() => Vertices.Count * RDPVtx.SIZE;
        }
        public class UnknowHolder : ObjectHolder
        {
            public byte[] Data { get; set; }

            public UnknowHolder(string name, byte[] data) : base(name) => Data = data;

            public override EntryType GetEntryType() => EntryType.Unknown;
            public override byte[] GetData() => Data;
            public override void SetData(byte[] data) => Data = data;
            public override int GetSize() => Data.Length;

        }
        public class TextureHolder : ObjectHolder
        {
            public byte[] Texture { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public N64TexFormat Format { get; set; }
            public TextureHolder Tlut { get; set; }


            public TextureHolder(string name, int w, int h, N64TexFormat format, byte[] tex) : base(name)
            {
                Width = w;
                Height = h;
                Format = format;
                Tlut = null;
                SetData(tex);
            }

            public void SetBitmap(Bitmap bmp, N64TexFormat format)
            {
                throw new NotImplementedException();
            }
            public Bitmap GetBitmap()
            {
                return N64Texture.DecodeBitmap(Width, Height, Format, Texture, Tlut?.Texture);
            }

            public override EntryType GetEntryType() => EntryType.Texture;
            public override byte[] GetData() => Texture;
            public override void SetData(byte[] data)
            {
                int validSize = N64Texture.GetTexSize(Width * Height, Format);
                if (data.Length != validSize)
                    throw new Z64ObjectException($"Invalid data size (0x{data.Length:X} instead of 0x{validSize:X})");

                Texture = data;
            }
            public override int GetSize() => Texture.Length;
        }

        public List<ObjectHolder> Entries { get; set; }

        public Z64Object()
        {
            Entries = new List<ObjectHolder>();
        }
        public Z64Object(byte[] data) : this()
        {
            AddUnknow(data.Length);
            SetData(data);
        }
        private bool HolderOverlaps(ObjectHolder holder, int holderOff)
        {
            int entryOff = 0;
            foreach (var entry in Entries)
            {
                if (holderOff >= entryOff && holderOff + holder.GetSize() <= entryOff + entry.GetSize() && entry.GetEntryType() == EntryType.Unknown)
                    return false;

                entryOff += entry.GetSize();
            }
            return true;
        }

        private bool VertexHolderOverlaps(VertexHolder holder, int holderOff)
        {
            int entryOff = 0;
            foreach (var entry in Entries)
            {
                if ((holderOff >= entryOff && holderOff < entryOff + entry.GetSize()) ||
                    (entryOff >= holderOff && entryOff + entry.GetSize() > holderOff + holder.GetSize()) ||
                    (holderOff <= entryOff && holderOff + holder.GetSize() >= entryOff + entry.GetSize())
                    )
                {
                    if (holder.GetEntryType() != EntryType.Vertex && holder.GetEntryType() != EntryType.Unknown)
                        return true;
                }
                entryOff += entry.GetSize();
            }
            return false;
        }

        private ObjectHolder AddHolder(ObjectHolder holder, int holderOff = -1)
        {
            if (holder.GetSize() <= 0)
                throw new Exception("Invalid holder size");

            if (holderOff == -1)
                holderOff = GetSize();

            if (holderOff == GetSize())
            {
                Entries.Add(holder);
                return holder;
            }
            else if (holderOff > GetSize())
            {
                AddUnknow(holderOff - GetSize());
                Entries.Add(holder);
                return holder;
            }
            else if (!HolderOverlaps(holder, holderOff))
            {
                int entryOff = 0;
                for (int i = 0; i < Entries.Count; i++)
                {
                    if (holderOff >= entryOff && (holderOff + holder.GetSize()) <= entryOff + Entries[i].GetSize())
                    {
                        int startDiff = holderOff - entryOff;
                        int endDiff = (entryOff + Entries[i].GetSize()) - (holderOff + holder.GetSize());

                        List<ObjectHolder> newEntries = new List<ObjectHolder>()
                        {
                           new UnknowHolder($"unk_{entryOff:X8}", new byte[startDiff]),
                           holder,
                           new UnknowHolder($"unk_{(holderOff+holder.GetSize()):X8}", new byte[endDiff]),
                        }.FindAll(e => e.GetSize() > 0);

                        Entries.RemoveAt(i);
                        Entries.InsertRange(i, newEntries);

                        break;
                    }

                    entryOff += Entries[i].GetSize();
                }
                return holder;
            }
            else
            {
                var existing = Entries.Find(e => e.GetSize() == holder.GetSize() && OffsetOf(e) == holderOff);
                if (existing != null)
                    return  existing;

                throw new Z64ObjectException($"Overlapping data (type={holder.GetEntryType()}, off=0x{holderOff:X}, size=0x{holder.GetSize():X})");

            }
        }
        // it's pretty common to see vertices overlap
        private void AddVertexHolder(VertexHolder holder, int holderOff = -1)
        {
            if (holder.GetSize() <= 0)
                return;

            if (holderOff == -1)
                holderOff = GetSize();

            if (holderOff == GetSize())
            {
                Entries.Add(holder);
            }
            else if (holderOff > GetSize())
            {
                AddUnknow(holderOff - GetSize());
                Entries.Add(holder);
            }
            else // Check if fits ?
            {
                int entryOff = 0;
                for (int i = 0; i < Entries.Count; i++)
                {
                    if (holderOff >= entryOff && holderOff < entryOff + Entries[i].GetSize())
                    {
                        if (Entries[i].GetEntryType() == EntryType.Vertex || Entries[i].GetEntryType() == EntryType.Unknown)
                        {
                            if (entryOff + Entries[i].GetSize() >= holderOff + holder.GetSize())
                            {
                                int startDiff = holderOff - entryOff;
                                int endDiff = (entryOff + Entries[i].GetSize()) - (holderOff + holder.GetSize());
                                if ((Entries[i].GetEntryType() == EntryType.Vertex && startDiff % 0x10 != 0) || (Entries[i].GetEntryType() == EntryType.Vertex && endDiff % 0x10 != 0))
                                    throw new Z64ObjectException("Invalid size for a vertex buffer");

                                List<ObjectHolder> newEntries = new List<ObjectHolder>()
                                {
                                   Entries[i].GetEntryType() == EntryType.Unknown
                                        ? (ObjectHolder)new UnknowHolder($"unk_{entryOff:X8}", new byte[startDiff])
                                        : new VertexHolder($"vtx_{entryOff:X8}", new RDPVtx[startDiff/0x10].ToList()),

                                   new VertexHolder($"vtx_{holderOff:X8}", holder.Vertices),

                                   Entries[i].GetEntryType() == EntryType.Unknown
                                        ? (ObjectHolder)new UnknowHolder($"unk_{entryOff:X8}", new byte[endDiff])
                                        : new VertexHolder($"vtx_{(holderOff+holder.GetSize()):X8}", new RDPVtx[endDiff/0x10].ToList()),
                                }.FindAll(e => e.GetSize() > 0);

                                Entries.RemoveAt(i);
                                Entries.InsertRange(i, newEntries);

                                newEntries.ForEach(o => entryOff += o.GetSize());

                                return;
                            }
                            else
                            {
                                int startDiff = holderOff - entryOff;
                                int endDiff = (entryOff + Entries[i].GetSize()) - holderOff;
                                if ((Entries[i].GetEntryType() == EntryType.Vertex && startDiff % 0x10 != 0) || (Entries[i].GetEntryType() == EntryType.Vertex && endDiff % 0x10 != 0))
                                    throw new Z64ObjectException("Invalid size for a vertex buffer");

                                List<ObjectHolder> newEntries = new List<ObjectHolder>()
                                {
                                   Entries[i].GetEntryType() == EntryType.Unknown
                                        ? (ObjectHolder)new UnknowHolder($"unk_{entryOff:X8}", new byte[startDiff])
                                        : new VertexHolder($"vtx_{entryOff:X8}", new RDPVtx[startDiff/0x10].ToList()),


                                   new VertexHolder($"vtx_{holderOff:X8}", new RDPVtx[endDiff/0x10].ToList()),
                                }.FindAll(e => e.GetSize() > 0);
                                holder = new VertexHolder($"vtx_{(entryOff + Entries[i].GetSize()):X8}", new RDPVtx[holder.Vertices.Count-(endDiff/0x10)].ToList());

                                Entries.RemoveAt(i);
                                Entries.InsertRange(i, newEntries);

                                newEntries.ForEach(o => entryOff += o.GetSize());

                                i += newEntries.Count - 1;
                                holderOff = entryOff;
                            }

                        }
                        else throw new Z64ObjectException($"Vertex did not fit (off=0x{holderOff:X}, size=0x{holder.GetSize():X})");
                    }
                    else
                    {
                        entryOff += Entries[i].GetSize();
                    }
                }

                if (holder.GetSize() > 0)
                    Entries.Add(holder);
            }
        }

        public DListHolder AddDList(int size, string name = null, int off = -1)
        {
            if (off == -1) off = GetSize();
            var holder = new DListHolder(name?? $"dlist_{off:X8}", new byte[size]);
            return (DListHolder)AddHolder(holder, off);
        }
        public UnknowHolder AddUnknow(int size, string name = null, int off = -1)
        {
            if (off == -1) off = GetSize();
            var holder = new UnknowHolder(name?? $"unk_{off:X8}", new byte[size]);
            return (UnknowHolder)AddHolder(holder, off);
        }
        public TextureHolder AddTexture(int w, int h, N64TexFormat format, string name = null, int off = -1)
        {
            if (off == -1) off = GetSize();
            var holder = new TextureHolder(name?? $"tex_{off:X8}", w, h, format, new byte[N64Texture.GetTexSize(w*h, format)]);
            return (TextureHolder)AddHolder(holder, off);
        }
        public VertexHolder AddVertices(int vtxCount, string name = null, int off = -1)
        {
            if (off == -1) off = GetSize();
            var holder = new VertexHolder(name?? $"vtx_{off:X8}", new RDPVtx[vtxCount].ToList());
            AddVertexHolder(holder, off);
            return holder;
        }


        public void FixNames()
        {
            int entryOff = 0;
            foreach (var entry in Entries)
            {
                entry.Name = (entry.GetEntryType() == EntryType.DList
                    ? "dlist_"
                    : entry.GetEntryType() == EntryType.Texture
                        ? "tex_"
                        : entry.GetEntryType() == EntryType.Vertex
                            ? "vtx_"
                            : "unk_") + entryOff.ToString("X8");

                entryOff += entry.GetSize();
            }
            foreach (var entry in Entries)
            {
                if (entry.GetEntryType() == EntryType.Texture)
                {
                    var tex = (TextureHolder)entry;
                    if (tex.Tlut != null)
                        tex.Tlut.Name = tex.Tlut.Name.Replace("tex", "tlut");
                }
            }
        }
        public void GroupUnkEntries()
        {
            int count = Entries.Count;
            for (int i = 1; i < count; i++)
            {
                if (Entries[i].GetEntryType() == EntryType.Unknown && Entries[i-1].GetEntryType() == EntryType.Unknown)
                {
                    List<byte> newData = Entries[i - 1].GetData().ToList();
                    newData.AddRange(Entries[i].GetData());
                    Entries[i - 1].SetData(newData.ToArray());
                    Entries.RemoveAt(i);
                    count--;
                    i--;
                }
            }
        }
        public bool IsOffsetFree(int off)
        {
            int entryOff = 0;
            foreach (var entry in Entries)
            {
                if (off >= entryOff && off < entryOff + entry.GetSize() && entry.GetEntryType() != EntryType.Unknown)
                    return false;
                entryOff += entry.GetSize();
            }
            return true;
        }
        public int GetSize()
        {
            int size = 0;
            foreach (var entry in Entries)
                size += entry.GetSize();
            return size;
        }
        public int OffsetOf(ObjectHolder holder)
        {
            int off = 0;
            for (int i = 0; i < Entries.IndexOf(holder); i++)
                off += Entries[i].GetSize();

            return off;
        }
        public void SetData(byte[] data)
        {
            //if (((data.Length + 0xF) & ~0xF) != ((GetSize()+0xF) & ~0xF))
            if (data.Length != GetSize())
                throw new Exception($"Invalid data size (0x{data.Length:X} instead of 0x{GetSize():X})");

            using (MemoryStream ms = new MemoryStream(data))
            {
                BinaryReader br = new BinaryReader(ms);

                foreach (var iter in Entries)
                    iter.SetData(br.ReadBytes(iter.GetSize()));
            }
        }
        public byte[] Build()
        {
            using (MemoryStream ms = new MemoryStream())
            {
                BinaryStream bw = new BinaryStream(ms);

                foreach (var entry in Entries)
                    bw.Write(entry.GetData());

                bw.Align(0x10, true);

                return ms.GetBuffer().Take((int)ms.Length).ToArray();
            }
        }


        private class JsonObjectHolder
        {
            public string Name { get; set; }
            [JsonConverter(typeof(JsonStringEnumConverter))]
            public EntryType EntryType { get; set; }
        }
        private class JsonUnknowHolder : JsonObjectHolder
        {
            public int Size { get; set; }
        }
        private class JsonUCodeHolder : JsonObjectHolder
        {
            public int Size { get; set; }
        }
        private class JsonVertexHolder : JsonObjectHolder
        {
            public int VertexCount { get; set; }
        }
        private class JsonTextureHolder : JsonObjectHolder
        {
            public int Width { get; set; }
            public int Height { get; set; }
            [JsonConverter(typeof(JsonStringEnumConverter))]
            public N64TexFormat Format { get; set; }
            public string Tlut { get; set; }
        }
        public string GetJSON()
        {
            var list = new List<object>();
            foreach (var iter in Entries)
            {
                switch (iter.GetEntryType())
                {
                    case EntryType.DList:
                        {
                            list.Add(new JsonUCodeHolder()
                            {
                                Name = iter.Name,
                                EntryType = iter.GetEntryType(),
                                Size = iter.GetSize()
                            });
                            break;
                        }
                    case EntryType.Vertex:
                        {
                            list.Add(new JsonVertexHolder()
                            {
                                Name = iter.Name,
                                EntryType = iter.GetEntryType(),
                                VertexCount = ((VertexHolder)iter).Vertices.Count,
                            });
                            break;
                        }
                    case EntryType.Texture:
                        {
                            var holder = (TextureHolder)iter;
                            list.Add(new JsonTextureHolder()
                            {
                                Name = iter.Name,
                                EntryType = iter.GetEntryType(),
                                Width = holder.Width,
                                Height = holder.Height,
                                Format = holder.Format,
                                Tlut = holder.Tlut?.Name,
                            });
                            break;
                        }
                    case EntryType.Unknown:
                        {
                            list.Add(new JsonUnknowHolder()
                            {
                                Name = iter.Name,
                                EntryType = iter.GetEntryType(),
                                Size = iter.GetSize()
                            });
                            break;
                        }
                    default:
                        throw new Z64ObjectException($"Invalid entry type ({iter.GetEntryType()})");
                }
            }
            return JsonSerializer.Serialize<object>(list, new JsonSerializerOptions() { WriteIndented = true }) ;
        }
        public static Z64Object FromJson(string json)
        {
            Z64Object obj = new Z64Object();
            var list = JsonSerializer.Deserialize<List<object>>(json);

            foreach (JsonElement iter in list)
            {
                var type = (EntryType)Enum.Parse(typeof(EntryType), iter.GetProperty(nameof(JsonObjectHolder.EntryType)).GetString());
                switch (type)
                {
                    
                    case EntryType.DList:
                        {
                            var holder = iter.ToObject<JsonUCodeHolder>();
                            obj.AddDList(holder.Size, holder.Name);
                            break;
                        }
                    case EntryType.Vertex:
                        {
                            var holder = iter.ToObject<JsonVertexHolder>();
                            obj.AddVertices(holder.VertexCount, holder.Name);
                            break;
                        }
                    case EntryType.Texture:
                        {
                            var holder = iter.ToObject<JsonTextureHolder>();
                            obj.AddTexture(holder.Width, holder.Height, holder.Format, holder.Name);
                            break;
                        }
                    case EntryType.Unknown:
                        {
                            var holder = iter.ToObject<JsonUnknowHolder>();
                            obj.AddUnknow(holder.Size, holder.Name);
                            break;
                        }
                    default: throw new Z64ObjectException($"Invalid entry type ({type})");
                }
            }
            for (int i = 0; i < list.Count; i++)
            {
                var holder = ((JsonElement)list[i]).ToObject<JsonTextureHolder>();
                if (holder.EntryType == EntryType.Texture)
                {
                    var tlut = (TextureHolder)obj.Entries.Find(e => e.GetEntryType() == EntryType.Texture && e.Name == holder.Tlut);
                    ((TextureHolder)obj.Entries[i]).Tlut = tlut;
                }
            }
            return obj;
        }

    }
}
