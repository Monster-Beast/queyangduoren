using System;
using System.Collections.Generic;
using System.IO;
using Queyang.Core;

namespace Queyang.Game
{
    internal sealed class ManagementPage
    {
        internal const string Prefix = "M1:";
        internal string Title;
        internal readonly List<KeyValuePair<string,string>> Rows = new List<KeyValuePair<string,string>>();
        internal ManagementPage(string title) { Title = title; }
        internal ManagementPage Add(string label,string route)
        {
            if (label.Length > 100) label = label.Substring(0,99) + "…";
            Rows.Add(new KeyValuePair<string,string>(label,route)); return this;
        }
        internal string Pack()
        {
            using (var stream = new MemoryStream())
            using (var w = new BinaryWriter(stream))
            {
                Queyang.Core.Wire.Text(w,Title); w.Write((byte)Rows.Count);
                foreach(var row in Rows) { Queyang.Core.Wire.Text(w,row.Key); Queyang.Core.Wire.Text(w,row.Value); }
                return Prefix + Convert.ToBase64String(stream.ToArray());
            }
        }
        internal static ManagementPage Unpack(string s)
        {
            using (var r = new BinaryReader(new MemoryStream(Convert.FromBase64String(s.Substring(Prefix.Length)))))
            {
                var page = new ManagementPage(Queyang.Core.Wire.Text(r));
                int count = r.ReadByte();
                if (count > 16) throw new InvalidDataException("Invalid menu");
                for (int i=0;i<count;i++) page.Add(Queyang.Core.Wire.Text(r),Queyang.Core.Wire.Text(r));
                Queyang.Core.Wire.End(r);
                return page;
            }
        }
    }
}
