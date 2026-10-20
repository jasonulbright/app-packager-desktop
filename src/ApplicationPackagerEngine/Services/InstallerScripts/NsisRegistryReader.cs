namespace ApplicationPackagerEngine.Services.InstallerScripts;

/// <summary>
/// Lists the registry writes and deletes in an NSIS installer's compiled script: every
/// WriteReg (opcode 51) and DeleteReg (opcode 50) instruction in code order, with the
/// registry view in effect at the instruction. Values stay in NSIS notation ($INSTDIR, $0).
/// </summary>
public static class NsisRegistryReader
{
    private static readonly Dictionary<int, string> TypeNames = new()
    {
        [0] = "REG_NONE", [1] = "REG_SZ", [2] = "REG_EXPAND_SZ", [3] = "REG_BINARY", [4] = "REG_DWORD", [7] = "REG_MULTI_SZ"
    };

    /// <summary>
    /// Reads at most <paramref name="maxRows"/> rows. The note is empty when the header
    /// decoded, every row fitted and no view was inferred; otherwise it says why rows are missing
    /// or which rows take the 64-bit view from .onInit.
    /// </summary>
    public static (List<RegistryWrite> Rows, string Note) Read(string path, int maxRows, string section = "", CancellationToken ct = default)
    {
        var rows = new List<RegistryWrite>();
        var ctx = NsisScriptReader.ReadHeaderTable(path);
        if (ctx.Header is null)
            return (rows, string.IsNullOrEmpty(ctx.Note) ? "The NSIS script header was not decoded." : ctx.Note);
        var h = ctx.Header;

        var sections = new List<(string Name, long Start, long End)>();
        if (ctx.SectionsCount > 0 && ctx.SectionSize >= 24)
        {
            for (var s = 0; s < ctx.SectionsCount && s < 100_000; s++)
            {
                var b = ctx.SectionsOffset + ((long)s * ctx.SectionSize);
                if (b + 24 > h.Length) break;
                var namePtr = BitConverter.ToInt32(h, (int)b);
                var code = BitConverter.ToInt32(h, (int)b + 12);
                var codeSize = BitConverter.ToInt32(h, (int)b + 16);
                var name = namePtr > 0 ? ctx.Str(namePtr) : "";
                if (string.IsNullOrEmpty(name)) name = "(unnamed section " + s + ")";
                sections.Add((name, code, (long)code + codeSize));
            }
        }
        string SectionOf(int index)
        {
            foreach (var sec in sections)
                if (index >= sec.Start && index < sec.End) return sec.Name;
            return "(function)";
        }

        var regView = new NsisRegView();
        var defaultViewRows = new List<int>();
        var limitNote = "";
        for (var e = 0; e < ctx.EntriesCount; e++)
        {
            if ((e & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
            var b = ctx.EntriesOffset + (e * 28);
            var op = BitConverter.ToInt32(h, b);
            if (op != 13 && op != 50 && op != 51) continue;
            var p0 = BitConverter.ToInt32(h, b + 4);
            var p1 = BitConverter.ToInt32(h, b + 8);
            var p2 = BitConverter.ToInt32(h, b + 12);
            var p3 = BitConverter.ToInt32(h, b + 16);
            var p4 = BitConverter.ToInt32(h, b + 20);
            var p5 = BitConverter.ToInt32(h, b + 24);
            if (op == 13)
            {
                regView.Apply(p0, p2, () => ctx.Str(p1));
                continue;
            }
            if (rows.Count >= maxRows)
            {
                // The walk goes on without rows so the script-wide SetRegView facts stay complete.
                limitNote = $"Only the first {maxRows} registry instructions are listed.";
                continue;
            }

            var root = NsisScriptReader.ResolveRegistryRoot(op == 51 ? p0 : p1);
            var forced = root.View is "32" or "64";
            var view = forced ? root.View : regView.Current;
            if (!forced && view.Length == 0) defaultViewRows.Add(rows.Count);
            var sectionName = string.IsNullOrEmpty(section) ? SectionOf(e) : section + ": " + SectionOf(e);
            var hive = root.Name == "SHCTX" ? "SHCTX" : root.Name;
            var condition = root.Name == "SHCTX" ? "HKLM after SetShellVarContext all, otherwise HKCU" : "";
            if (op == 51)
            {
                var value = p4 is 1 or 4 ? ctx.Str(p3) : "(data block)";
                rows.Add(new RegistryWrite("NsisScript", hive, ctx.Str(p1), ctx.Str(p2), value,
                    TypeNames.TryGetValue(p5, out var t) ? t : "type " + p5, condition)
                {
                    Action = "write",
                    View = view,
                    Section = sectionName
                });
            }
            else
            {
                var deleteKey = p4 != 0;
                var flags = new List<string>();
                if (((p4 >> 1) & 1) != 0) flags.Add("/ifempty");
                if (((p4 >> 1) & 2) != 0) flags.Add("/ifnovalues");
                rows.Add(new RegistryWrite("NsisScript", hive, ctx.Str(p2), deleteKey ? "" : ctx.Str(p3), "", "",
                    string.Join("; ", new[] { condition, string.Join(" ", flags) }.Where(x => x.Length > 0)))
                {
                    Action = deleteKey ? "delete key" : "delete value",
                    View = view,
                    Section = sectionName
                });
            }
        }

        var notes = new List<string>();
        if (regView.InitSets64 && defaultViewRows.Count > 0)
        {
            foreach (var index in defaultViewRows) rows[index] = rows[index] with { View = "64" };
            notes.Add("The script's last SetRegView is 64 and it never selects 32, so SetRegView 64 runs in .onInit: rows before it in code order are shown in the 64-bit view.");
        }
        if (limitNote.Length > 0) notes.Add(limitNote);
        return (rows, string.Join(" ", notes));
    }
}
