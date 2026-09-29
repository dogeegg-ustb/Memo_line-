using System.Runtime.InteropServices;
using System.Text;

namespace CSPevent;

internal sealed record StringEntry(string Label, string Normalized, string Compact,
    string SimplifiedCompact,
    string ResourceFile, string NodePath);

internal sealed class StringCatalog
{
    private readonly Dictionary<string, List<StringEntry>> _byLanguage = new();

    internal StringCatalog(string databasePath)
    {
        if (!File.Exists(databasePath)) throw new FileNotFoundException("词条数据库不存在", databasePath);
        byte[] path = Encoding.UTF8.GetBytes(databasePath + "\0");
        Check(Sqlite.Open(path, out IntPtr db, 1), db, "打开数据库");
        try
        {
            const string query = "SELECT text, normalized_text, resource_file, node_path " +
                "FROM resource_strings WHERE language=? AND length(text)<=64 " +
                "AND instr(text,char(10))=0 AND instr(text,char(13))=0";
            byte[] sql = Encoding.UTF8.GetBytes(query + "\0");
            Check(Sqlite.Prepare(db, sql, -1, out IntPtr statement, IntPtr.Zero), db, "查询词条");
            try
            {
                foreach (string language in new[] { "chinese_tc", "japanese", "english" })
                {
                    var result = new List<StringEntry>();
                    Sqlite.Reset(statement);
                    Sqlite.ClearBindings(statement);
                    byte[] value = Encoding.UTF8.GetBytes(language);
                    Check(Sqlite.BindText(statement, 1, value, value.Length, new IntPtr(-1)), db, "设置语言");
                    int status;
                    while ((status = Sqlite.Step(statement)) == 100)
                    {
                        string label = Read(statement, 0);
                        string normalized = Read(statement, 1);
                        result.Add(new StringEntry(label, normalized, Compact(normalized),
                            language == "chinese_tc" ? Compact(ToSimplified(label)) : "",
                            Read(statement, 2), Read(statement, 3)));
                    }
                    if (status != 101) Check(status, db, "读取词条");
                    _byLanguage[language] = result;
                }

                // 自动构建简体中文 (chinese_sc) 词库
                if (_byLanguage.TryGetValue("chinese_tc", out var tcList))
                {
                    var scList = new List<StringEntry>(tcList.Count + 64);
                    foreach (var tc in tcList)
                    {
                        string simple = ToSimplified(tc.Label);
                        string mapped = ApplyTerminology(simple);
                        string stripped = StripAccelerator(mapped);

                        scList.Add(new StringEntry(
                            mapped,
                            Normalize(mapped),
                            Compact(mapped),
                            Compact(stripped),
                            tc.ResourceFile,
                            tc.NodePath));

                        if (mapped != simple)
                        {
                            string simpleStripped = StripAccelerator(simple);
                            scList.Add(new StringEntry(
                                simple,
                                Normalize(simple),
                                Compact(simple),
                                Compact(simpleStripped),
                                tc.ResourceFile,
                                tc.NodePath));
                        }
                    }

                    // 核心顶级菜单项注入（包含括号快捷键与去括号单字形式）
                    var topMenus = new[] {
                        ("文件(F)", "File"), ("编辑(E)", "Edit"), ("动画(A)", "Animation"),
                        ("图层(L)", "Layer"), ("选区(S)", "Selection"), ("视图(V)", "View"),
                        ("滤镜(I)", "Filter"), ("窗口(W)", "Window"), ("帮助(H)", "Help")
                    };
                    foreach (var (menuLabel, hint) in topMenus)
                    {
                        string stripped = StripAccelerator(menuLabel);
                        scList.Add(new StringEntry(
                            menuLabel,
                            Normalize(menuLabel),
                            Compact(menuLabel),
                            Compact(stripped),
                            "top_menu",
                            $"menu.{hint}"));
                    }

                    _byLanguage["chinese_sc"] = scList;
                }
            }
            finally { Sqlite.Finalize(statement); }
        }
        finally { Sqlite.Close(db); }
    }

    private static string Read(IntPtr statement, int column) =>
        Marshal.PtrToStringUTF8(Sqlite.ColumnText(statement, column)) ?? "";

    private static void Check(int status, IntPtr db, string operation)
    {
        if (status == 0) return;
        throw new InvalidOperationException($"{operation}失败：{Marshal.PtrToStringUTF8(Sqlite.Error(db))}");
    }

    internal int Count(string language) =>
        _byLanguage.TryGetValue(language, out var list) ? list.Count : 0;

    internal MatchResult Match(string language, string observed)
    {
        string fragment = Compact(observed);
        int min = language == "english" ? 3 : 2;
        if (fragment.Length < min) return new(null, 0, "文字过短");
        if (!_byLanguage.TryGetValue(language, out var entries))
            entries = _byLanguage.TryGetValue("chinese_sc", out var fallback) ? fallback : _byLanguage.Values.First();

        var found = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        foreach (StringEntry entry in entries)
        {
            if (entry.Compact.Length < min) continue;
            double score = Math.Max(Score(fragment, entry.Compact),
                entry.SimplifiedCompact.Length > 0 ? Score(fragment, entry.SimplifiedCompact) : 0);
            if (score >= 0.48) Add(found, entry, score);
        }
        // A small inserted or misread glyph is common on narrow CJK crops.
        foreach (StringEntry entry in entries)
        {
            if (!IsCommand(entry) || entry.Compact.Length < 3) continue;
            double score = Math.Max(FuzzyScore(fragment, entry.Compact),
                entry.SimplifiedCompact.Length > 0 ?
                    FuzzyScore(fragment, entry.SimplifiedCompact) : 0);
            if (score < 0.70) continue;
            if (found.TryGetValue(entry.Compact, out Candidate? existing))
                existing.Score = Math.Max(existing.Score, score);
            else Add(found, entry, score);
        }
        var candidates = found.Values
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Command)
            .ThenBy(item => item.Length)
            .Take(8).ToList();
        if (candidates.Count == 0) return new(null, 0, "词库未匹配");
        var best = candidates[0];
        bool ambiguous = candidates.Count > 1 && candidates[1].Score >= best.Score - 0.06;
        string state = ambiguous ? "候选" : best.Score >= 0.99 ? "匹配" : "部分匹配";
        if (best.Keys > 1) state += "（多处同名）";
        return new(best.Label, found.Count, state);
    }

    private sealed class Candidate
    {
        internal required string Label;
        internal required double Score;
        internal required int Length;
        internal bool Command;
        internal int Keys;
    }

    private static void Add(Dictionary<string, Candidate> found, StringEntry entry,
        double score)
    {
        if (found.TryGetValue(entry.Compact, out Candidate? existing))
        {
            existing.Score = Math.Max(existing.Score, score);
            existing.Command |= IsCommand(entry);
            existing.Keys++;
        }
        else found[entry.Compact] = new Candidate {
            Label = entry.Label, Score = score, Length = entry.Compact.Length,
            Command = IsCommand(entry), Keys = 1
        };
    }

    private static bool IsCommand(StringEntry entry) =>
        entry.ResourceFile.Equals("742DEA58-ED6B-4402-BC11-20DFC6D08040", StringComparison.OrdinalIgnoreCase) &&
        (entry.NodePath.StartsWith("1.10.1.", StringComparison.Ordinal) ||
         entry.NodePath.StartsWith("1.349.1.", StringComparison.Ordinal));

    private static string Normalize(string value) =>
        string.Join(' ', value.Normalize(NormalizationForm.FormKC)
            .ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Compact(string value) => new(value.Normalize(NormalizationForm.FormKC)
        .ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string ToSimplified(string value)
    {
        int length = LCMapStringEx("zh-CN", 0x02000000, value, value.Length,
            null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (length <= 0) return value;
        var output = new char[length];
        return LCMapStringEx("zh-CN", 0x02000000, value, value.Length,
            output, output.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) > 0
            ? new string(output) : value;
    }

    private static readonly (string From, string To)[] CspTerminologyMap = new[]
    {
        ("检视", "视图"),
        ("视窗", "窗口"),
        ("说明", "帮助"),
        ("选取范围", "选区"),
        ("选取", "选区"),
        ("档案", "文件"),
        ("偏好设定", "首选项"),
        ("环境设定", "首选项"),
        ("快捷键设定", "快捷键设置"),
        ("指令列", "命令栏"),
        ("辅助工具", "子工具"),
        ("色彩快显", "快捷色板"),
        ("网点", "网点"),
        ("沾水笔", "蘸水笔"),
    };

    private static string ApplyTerminology(string text)
    {
        string result = text;
        foreach (var (from, to) in CspTerminologyMap)
        {
            if (result.Contains(from))
                result = result.Replace(from, to);
        }
        return result;
    }

    private static string StripAccelerator(string value)
    {
        int paren = value.LastIndexOf('(');
        if (paren >= 0 && value.EndsWith(')'))
        {
            string inside = value.Substring(paren + 1, value.Length - paren - 2).TrimStart('&');
            if (inside.Length <= 2) return value.Substring(0, paren).Trim();
        }
        return value;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int LCMapStringEx(string localeName, uint flags,
        string source, int sourceLength, [Out] char[]? destination, int destinationLength,
        IntPtr version, IntPtr reserved, IntPtr sortHandle);

    private static double Score(string observed, string label)
    {
        if (observed == label) return 1;
        if (observed.Contains(label, StringComparison.Ordinal))
            return 0.92 * label.Length / observed.Length;
        if (label.Contains(observed, StringComparison.Ordinal))
            return 0.82 * observed.Length / label.Length;
        return 0;
    }

    private static double FuzzyScore(string observed, string label)
    {
        if (Math.Abs(observed.Length - label.Length) > 1 ||
            observed.Length > 64 || label.Length > 64) return 0;
        Span<int> previous = stackalloc int[65];
        Span<int> current = stackalloc int[65];
        for (int j = 0; j <= label.Length; j++) previous[j] = j;
        for (int i = 1; i <= observed.Length; i++)
        {
            current[0] = i;
            int rowMin = current[0];
            for (int j = 1; j <= label.Length; j++)
            {
                int cost = observed[i - 1] == label[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1,
                    previous[j] + 1), previous[j - 1] + cost);
                rowMin = Math.Min(rowMin, current[j]);
            }
            if (rowMin > 1) return 0;
            var temp = previous; previous = current; current = temp;
        }
        int edits = previous[label.Length];
        return edits <= 1 ? 1.0 - (double)edits / Math.Max(observed.Length, label.Length) : 0;
    }

    private static class Sqlite
    {
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Open(byte[] name, out IntPtr db, int flags, IntPtr vfs = default);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Close(IntPtr db);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Prepare(IntPtr db, byte[] sql, int length, out IntPtr statement, IntPtr tail);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_bind_text", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int BindText(IntPtr statement, int index, byte[] value, int length, IntPtr destructor);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Step(IntPtr statement);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_reset", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Reset(IntPtr statement);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_clear_bindings", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ClearBindings(IntPtr statement);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_text", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr ColumnText(IntPtr statement, int column);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Finalize(IntPtr statement);
        [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_errmsg", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr Error(IntPtr db);
    }
}

internal sealed record MatchResult(string? Label, int CandidateCount, string State);
