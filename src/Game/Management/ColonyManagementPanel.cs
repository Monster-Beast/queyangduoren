using System;
using System.IO;
using UnityEngine;

namespace Queyang.Game
{
    internal static class ColonyManagementPanel
    {
        internal static bool IsOpen { get; private set; }
        static ManagementPage current;
        static string status;
        static bool waiting;
        static Vector2 scroll;
        static float requestedAt;
        static void Request(string route)
        {
            waiting=true;
            requestedAt=Time.unscaledTime;
            CoopSession.Command("manage/"+route,0,0);
        }
        static void Receive(string result)
        {
            if (!result.StartsWith(ManagementPage.Prefix,StringComparison.Ordinal)) return;
            try
            {
                current=ManagementPage.Unpack(result);
                status=null;
                scroll=Vector2.zero;
            }
            catch (Exception e) when (e is IOException || e is FormatException || e is InvalidDataException)
            { status="无法读取房主返回的菜单"; }
            waiting=false;
        }
        public static void Close()
        {
            CoopSession.CommandResult-=Receive;
            IsOpen=false; current=null; status=null; waiting=false;
        }
        public static void Draw(Rect entry)
        {
            if (!IsOpen)
            {
                if (GUI.Button(entry,"殖民地管理"))
                {
                    IsOpen=true;
                    CoopSession.CommandResult+=Receive;
                    Request("home");
                }
                return;
            }
            var rect = new Rect(12,12,Mathf.Min(440,Screen.width-24),Mathf.Min(380,Screen.height-24));
            GUI.Box(rect,GUIContent.none);
            GUILayout.BeginArea(new Rect(rect.x+8,rect.y+8,rect.width-16,rect.height-16));
            GUILayout.BeginHorizontal();
            GUILayout.Label(current?.Title ?? "殖民地管理");
            if (GUILayout.Button("关闭",GUILayout.Width(60))) Close();
            GUILayout.EndHorizontal();
            if (waiting && Time.unscaledTime-requestedAt>10) { waiting=false; status="请求超时，可刷新重试"; }
            if (waiting) GUILayout.Label("正在等待房主");
            if (status!=null) GUILayout.Label(status);
            scroll=GUILayout.BeginScrollView(scroll);
            if (current!=null)
            {
                foreach (var row in current.Rows)
                {
                    if (GUILayout.Button(row.Key) && !waiting) Request(row.Value);
                }
            }
            GUILayout.EndScrollView();
            if (GUILayout.Button("刷新首页") && !waiting && IsOpen) Request("home");
            GUILayout.EndArea();
        }
    }
}
