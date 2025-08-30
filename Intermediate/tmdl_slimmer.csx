// TMDL Slimmer (TE2)
// ------------------------------------------------------------
// Purpose
//   Produce a single ".slimdl" file from a TMDL semantic model,
//   removing noisy UI/engine metadata while preserving semantics
//   that matter to reasoning (e.g., relationships, keys, uniqueness).
//
// Behavior
//   - Prompts for the SemanticModel (or its "definition" folder).
//   - Reads all *.tmdl under /definition.
//   - Strips targeted properties (see pattern list).
//   - Skips whole blocks for "extendedProperties" and "linguisticMetadata".
//   - Preserves comments; removes ALL blank lines; trims trailing spaces.
//   - Writes UTF-8 without BOM.
//   - Prints a summary with basic stats.
//
// Notes
//   - TE2-friendly: no classes, no local methods, no LINQ.
//   - Boolean regex matches "prop = true/false", "prop: true/false",
//     or bare "prop" with optional ";".
//   - Keepers: isActive (relationship), isKey/isUnique (columns).
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

// ---------------- Configuration toggles ----------------
// Turn off any group below if you want to keep that metadata.
bool REMOVE_Annotations  = true; // annotation, changedProperty, extendedProperties
bool REMOVE_Lineage      = true; // lineageTag, sourceLineageTag
bool REMOVE_LanguageData = true; // cultures folder, culture, linguisticMetadata
bool REMOVE_ColumnMeta   = true; // summarizeBy, sourceColumn, dataCategory (+ select column booleans)
bool REMOVE_InferredMeta = true; // isNameInferred, isDataTypeInferred, sourceProviderType
bool REMOVE_DisplayProps = true; // isHidden, displayFolder, formatString, isDefaultLabel/Image

try
{
    // ---------- Select the SemanticModel folder ----------
    string modelFolder = null;
    using (var dialog = new FolderBrowserDialog())
    {
        dialog.Description = "Select the SemanticModel folder (contains 'definition' subfolder)";
        dialog.ShowNewFolderButton = false;
        if (dialog.ShowDialog() != DialogResult.OK) return;
        modelFolder = dialog.SelectedPath;
    }

    // ---------- Locate the /definition root ----------
    string definitionPath = Path.Combine(modelFolder, "definition");
    if (!Directory.Exists(definitionPath))
    {
        // Fallback: user selected the /definition folder directly or a flat export
        definitionPath = modelFolder;
        if (Directory.GetFiles(definitionPath, "*.tmdl", SearchOption.AllDirectories).Length == 0)
        {
            Info("No TMDL files found in selected folder.");
            return;
        }
    }

    // ---------- Build removal patterns (compact, same logic) ----------
    var patterns = new List<KeyValuePair<string, Regex>>();
    RegexOptions RX = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    // Booleans may be "prop = true/false", "prop: true/false", or bare "prop" with optional ";"
    string BOOL = @"(?:\s*(?:=|:)\s*(?:true|false))?\s*;?\s*$";

    // Tiny helper to add a rule when its toggle is ON
    Action<bool,string,string> Add = (on, key, pat) => { if (on) patterns.Add(new KeyValuePair<string, Regex>(key, new Regex(pat, RX))); };

    // Annotations / misc
    Add(REMOVE_Annotations,  "annotation",         @"^\s*annotation\b");
    Add(REMOVE_Annotations,  "changedProperty",    @"^\s*changedProperty\b");
    Add(REMOVE_Annotations,  "extendedProperties", @"^\s*extendedProperties\s*(?:=|:)\s*\{?");

    // Lineage
    Add(REMOVE_Lineage,      "lineageTag",         @"^\s*lineageTag\s*(?:=|:)");
    Add(REMOVE_Lineage,      "sourceLineageTag",   @"^\s*sourceLineageTag\s*(?:=|:)");

    // Language
    Add(REMOVE_LanguageData, "culture",            @"^\s*culture\s*(?:=|:)");
    Add(REMOVE_LanguageData, "refCulture",         @"^\s*ref\s+cultureInfo\b");
    Add(REMOVE_LanguageData, "linguisticMetadata", @"^\s*linguisticMetadata\s*(?:=|:)\s*\{?");

    // Column metadata (non-boolean)
    Add(REMOVE_ColumnMeta,   "dataCategory",       @"^\s*dataCategory\s*(?:=|:)");
    Add(REMOVE_ColumnMeta,   "summarizeBy",        @"^\s*summarizeBy\s*(?:=|:)");
    Add(REMOVE_ColumnMeta,   "sourceColumn",       @"^\s*sourceColumn\s*(?:=|:)");

    // Column / engine booleans to strip (keep isKey/isUnique)
    Add(REMOVE_ColumnMeta,   "isAvailableInMdx",   @"^\s*isAvailableInMdx" + BOOL);
    Add(REMOVE_ColumnMeta,   "isNullable",         @"^\s*isNullable" + BOOL);

    // Inferred / engine
    Add(REMOVE_InferredMeta, "isNameInferred",     @"^\s*isNameInferred" + BOOL);
    Add(REMOVE_InferredMeta, "isDataTypeInferred", @"^\s*isDataTypeInferred" + BOOL);
    Add(REMOVE_InferredMeta, "sourceProviderType", @"^\s*sourceProviderType\s*(?:=|:)");

    // Presentation (UI)
    Add(REMOVE_DisplayProps, "isHidden",           @"^\s*isHidden" + BOOL);
    Add(REMOVE_DisplayProps, "displayFolder",      @"^\s*displayFolder\s*(?:=|:)");
    Add(REMOVE_DisplayProps, "formatString",       @"^\s*formatString\s*(?:=|:)");
    Add(REMOVE_DisplayProps, "isDefaultLabel",     @"^\s*isDefaultLabel" + BOOL);
    Add(REMOVE_DisplayProps, "isDefaultImage",     @"^\s*isDefaultImage" + BOOL);

    // Block starters that should be skipped entirely { ... } until braces close
    var blockStarters = new HashSet<string>();
    if (REMOVE_LanguageData) blockStarters.Add("linguisticMetadata");
    if (REMOVE_Annotations)  blockStarters.Add("extendedProperties");

    // ---------- Stats store ----------
    var stats = new Dictionary<string, int>();

    // ---------- Find and sort all TMDL files (no LINQ) ----------
    string[] files = Directory.GetFiles(definitionPath, "*.tmdl", SearchOption.AllDirectories);
    if (files.Length == 0)
    {
        Info("No TMDL files found in the selected folder.");
        return;
    }
    // (Optional simplification applied: no explicit Array.Sort)

    // ---------- Combined output buffer ----------
    var output = new StringBuilder();
    output.AppendLine("// Combined TMDL (Slim)");
    output.AppendLine("// Source: " + Path.GetFileName(modelFolder));
    output.AppendLine("// Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

    long originalSize = 0;
    int filesProcessed = 0;

    // Base path to produce relative names quickly (used only for the cultures/ skip)
    string defBase = definitionPath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

    // ---------- Process each file ----------
    for (int fi = 0; fi < files.Length; fi++)
    {
        string filePath = files[fi];
        string rel = filePath.StartsWith(defBase)
            ? filePath.Substring(defBase.Length)
            : Path.GetFileName(filePath);
        rel = rel.Replace('\\', '/');

        // Skip entire cultures/ subtree when language data removal is enabled
        if (REMOVE_LanguageData && rel.StartsWith("cultures/"))
        {
            if (!stats.ContainsKey("cultures-folder")) stats["cultures-folder"] = 0;
            stats["cultures-folder"]++;
            continue;
        }

        // Read file and accumulate original size
        string content = File.ReadAllText(filePath, Encoding.UTF8);
        originalSize += new FileInfo(filePath).Length;

        // Process line-by-line with simple block skipping
        string[] lines = content.Split(new string[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        bool skipBlock = false;
        int braceDepth = 0;
        bool wroteAny = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];

            // If skipping a block, adjust brace depth until we exit
            if (skipBlock)
            {
                for (int k = 0; k < line.Length; k++)
                {
                    char ch = line[k];
                    if (ch == '{') braceDepth++;
                    else if (ch == '}') braceDepth--;
                }
                if (braceDepth <= 0) { skipBlock = false; braceDepth = 0; }
                continue;
            }

            // Determine whether to remove the line (or start skipping a block)
            bool removeLine = false;
            for (int p = 0; p < patterns.Count; p++)
            {
                var kv = patterns[p];
                if (kv.Value.IsMatch(line))
                {
                    // Start a block skip if this key is a known block starter
                    if (blockStarters.Contains(kv.Key))
                    {
                        // Stat tracking
                        if (!stats.ContainsKey(kv.Key)) stats[kv.Key] = 0;
                        stats[kv.Key]++;

                        // Compute initial brace delta on the same line
                        int delta = 0;
                        for (int k = 0; k < line.Length; k++)
                        {
                            char ch = line[k];
                            if (ch == '{') delta++;
                            else if (ch == '}') delta--;
                        }

                        braceDepth = delta;
                        skipBlock  = braceDepth > 0; // if no opening '{', treat as single-line removal
                        removeLine = true;
                        break;
                    }
                    else
                    {
                        // Simple single-line removal
                        if (!stats.ContainsKey(kv.Key)) stats[kv.Key] = 0;
                        stats[kv.Key]++;
                        removeLine = true;
                        break;
                    }
                }
            }

            if (!removeLine)
            {
                // Trim trailing spaces/tabs on this line (keeps comments intact)
                string trimmedRight = Regex.Replace(line, @"[ \t]+$", "");
                output.AppendLine(trimmedRight);
                wroteAny = true;
            }
        }

        if (wroteAny) filesProcessed++;
    }

    // Normalize final text: trim EOL whitespace, remove all blank lines, ensure single trailing newline.
    string finalOut = output.ToString();
    finalOut = Regex.Replace(Regex.Replace(finalOut, @"[ \t]+\r?\n", "\n"), @"(?m)^\s*\r?\n", "").Trim() + Environment.NewLine;

    // ---------- Save as .slimdl (UTF-8 without BOM) ----------
    var parent = Directory.GetParent(modelFolder);
    string suggested = Path.Combine(parent != null ? parent.FullName : modelFolder,
                                    Path.GetFileName(modelFolder) + ".slimdl");

    string outputPath;
    using (var sfd = new SaveFileDialog())
    {
        sfd.Title = "Save slimmed TMDL";
        sfd.Filter = "Slimmed TMDL (*.slimdl)|*.slimdl|Text files (*.tmdl;*.txt)|*.tmdl;*.txt|All files (*.*)|*.*";
        sfd.DefaultExt = "slimdl";
        sfd.AddExtension = true;
        sfd.FileName = Path.GetFileName(suggested);
        sfd.InitialDirectory = Path.GetDirectoryName(suggested);
        sfd.OverwritePrompt = true;
        sfd.CheckPathExists = true;
        if (sfd.ShowDialog() != DialogResult.OK) return;
        outputPath = sfd.FileName;
    }

    File.WriteAllText(outputPath, finalOut, new UTF8Encoding(false));

    // ---------- Summary / stats ----------
    long newSize = new FileInfo(outputPath).Length;
    double reduction = (originalSize > 0) ? (1.0 - (double)newSize / (double)originalSize) * 100.0 : 0.0;

    var summary = new StringBuilder();
    summary.AppendLine("TMDL Slimmer Results");
    summary.AppendLine("====================");
    summary.AppendLine(string.Format("Files processed: {0} of {1}", filesProcessed, files.Length));
    summary.AppendLine(string.Format("Input size:  {0:N1} KB", originalSize / 1024.0));
    summary.AppendLine(string.Format("Output: {0} ({1:N1} KB)", Path.GetFileName(outputPath), newSize / 1024.0));
    summary.AppendLine(string.Format("Size reduction: {0:F1}%", reduction));

    if (stats.Count > 0)
    {
        int total = 0; foreach (var v in stats.Values) total += v;
        summary.AppendLine();
        summary.AppendLine(string.Format("Removed {0:N0} items:", total));

        // Sort keys without LINQ
        var keys = new List<string>(stats.Keys);
        keys.Sort();
        for (int i = 0; i < keys.Count; i++)
            summary.AppendLine(string.Format("  - {0}: {1:N0}", keys[i], stats[keys[i]]));
    }

    Info(summary.ToString());
}
catch (Exception ex)
{
    Error("Processing failed: " + ex.Message);
}
