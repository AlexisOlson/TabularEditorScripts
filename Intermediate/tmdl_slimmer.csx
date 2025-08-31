//
// Title: TMDL Slimmer - Strip metadata bloat for LLM context
//
// Author: Alexis Olson
// Version: 1.1
//
// Description:
//   Reads all *.tmdl files from a SemanticModel/definition folder,
//   removes UI/engine metadata while preserving model semantics,
//   and outputs a single .slimdl file for LLM consumption.
//
// Usage:
//   - Run in Tabular Editor 2 or 3 (Advanced Scripting)
//   - Select your SemanticModel folder when prompted
//   - Choose where to save the output .slimdl file

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

// ==================== CONFIGURATION ====================
bool REMOVE_Annotations  = true; // annotation, changedProperty, extendedProperty/extendedProperties
bool REMOVE_Lineage      = true; // lineageTag, sourceLineageTag
bool REMOVE_LanguageData = true; // cultures folder (includes linguisticMetadata)
bool REMOVE_ColumnMeta   = true; // summarizeBy, sourceColumn, dataCategory (+ select column booleans)
bool REMOVE_InferredMeta = true; // isNameInferred, isDataTypeInferred, sourceProviderType
bool REMOVE_DisplayProps = true; // isHidden, displayFolder, formatString, isDefaultLabel/Image

// ==================== MAIN EXECUTION ====================
try
{
    // Select SemanticModel folder
    string modelFolder = null;
    using (var dialog = new FolderBrowserDialog())
    {
        dialog.Description = "Select the SemanticModel folder (contains 'definition' subfolder)";
        dialog.ShowNewFolderButton = false;
        if (dialog.ShowDialog() != DialogResult.OK) return;
        modelFolder = dialog.SelectedPath;
    }

    // Locate definition root - handle both cases: user selected SemanticModel or definition directly
    string definitionPath = Path.Combine(modelFolder, "definition");
    if (!Directory.Exists(definitionPath))
    {
        definitionPath = modelFolder; // Fallback: user already selected the definition folder
        if (Directory.GetFiles(definitionPath, "*.tmdl", SearchOption.AllDirectories).Length == 0)
        {
            Info("No TMDL files found in the selected folder.");
            return;
        }
    }

    // Build removal patterns based on configuration flags
    var patterns = new Dictionary<string, Regex>();

    // Common regex components for matching property assignments
    string ASSIGN = @"\s*(?:=|:)"; // matches optional whitespace then = or :
    string BOOL   = @"(?:\s*(?:=|:)\s*(?:true|false))?\s*;?\s*$"; // matches optional boolean and semicolon

    // Helper to add patterns when corresponding removal flag is enabled
    Action<bool,string,string> Add = (flag, name, pattern) =>
    {
        if (flag) patterns[name] = new Regex(pattern);
    };

    // Annotations group
    Add(REMOVE_Annotations,  "annotation",         @"^\s*annotation\b");
    Add(REMOVE_Annotations,  "changedProperty",    @"^\s*changedProperty\b");
    Add(REMOVE_Annotations,  "extendedProperty",   @"^\s*extendedPropert(?:y|ies)\b");

    // Lineage tracking group
    Add(REMOVE_Lineage,      "lineageTag",         @"^\s*lineageTag" + ASSIGN);
    Add(REMOVE_Lineage,      "sourceLineageTag",   @"^\s*sourceLineageTag" + ASSIGN);

    // Column metadata group
    Add(REMOVE_ColumnMeta,   "dataCategory",       @"^\s*dataCategory" + ASSIGN);
    Add(REMOVE_ColumnMeta,   "summarizeBy",        @"^\s*summarizeBy" + ASSIGN);
    Add(REMOVE_ColumnMeta,   "sourceColumn",       @"^\s*sourceColumn" + ASSIGN);
    Add(REMOVE_ColumnMeta,   "isAvailableInMdx",   @"^\s*isAvailableInMdx" + BOOL);
    Add(REMOVE_ColumnMeta,   "isNullable",         @"^\s*isNullable" + BOOL);

    // Inferred metadata group
    Add(REMOVE_InferredMeta, "isNameInferred",     @"^\s*isNameInferred" + BOOL);
    Add(REMOVE_InferredMeta, "isDataTypeInferred", @"^\s*isDataTypeInferred" + BOOL);
    Add(REMOVE_InferredMeta, "sourceProviderType", @"^\s*sourceProviderType" + ASSIGN);

    // Display/UI properties group
    Add(REMOVE_DisplayProps, "isHidden",           @"^\s*isHidden" + BOOL);
    Add(REMOVE_DisplayProps, "displayFolder",      @"^\s*displayFolder" + ASSIGN);
    Add(REMOVE_DisplayProps, "formatString",       @"^\s*formatString" + ASSIGN);
    Add(REMOVE_DisplayProps, "isDefaultLabel",     @"^\s*isDefaultLabel" + BOOL);
    Add(REMOVE_DisplayProps, "isDefaultImage",     @"^\s*isDefaultImage" + BOOL);

    // Identify patterns that start multi-line blocks (need brace tracking)
    var blockStarters = new HashSet<string>();
    if (REMOVE_Annotations)  {
        blockStarters.Add("extendedProperty");
    }

    // Track removal statistics for summary report
    var removalStats = new Dictionary<string, int>();

    // Small helper to increment removal counters deterministically
    Action<string> Bump = key =>
    {
        int v;
        if (!removalStats.TryGetValue(key, out v)) v = 0; removalStats[key] = v + 1;
    };
    
    // Collect all TMDL files recursively
    string[] tmdlFiles = Directory.GetFiles(definitionPath, "*.tmdl", SearchOption.AllDirectories);
    Array.Sort(tmdlFiles);
    if (tmdlFiles.Length == 0)
    {
        Info("No TMDL files found in the selected folder.");
        return;
    }

    // Initialize output with header
    var output = new StringBuilder();
    output.AppendLine("// Combined TMDL (Slim)");
    output.AppendLine("// Source: " + Path.GetFileName(modelFolder));
    output.AppendLine("// Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

    long originalTotalSize = 0;
    long culturesBytesRemoved = 0; // bytes saved by excluding cultures/ folder
    int culturesFilesSkipped = 0;  // number of cultures/ tmdl files skipped
    int filesWithContent = 0;

    // Calculate base path for relative file names (normalize with trailing separator)
    string definitionBasePath = definitionPath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

    // Process each TMDL file
    foreach (string filePath in tmdlFiles)
    {
        // Calculate relative path from definition root
        string relativePath = filePath.StartsWith(definitionBasePath)
            ? filePath.Substring(definitionBasePath.Length)
            : Path.GetFileName(filePath);
        relativePath = relativePath.Replace('\\', '/');

        // Include every file's size in input total, even if we skip its content later
        long fileSize = new FileInfo(filePath).Length;
        originalTotalSize += fileSize;

        // Skip entire cultures/ subtree when language data removal is enabled
        if (REMOVE_LanguageData && relativePath.StartsWith("cultures/"))
        {
            culturesBytesRemoved += fileSize; // track savings from cultures folder
            culturesFilesSkipped++;
            continue;
        }

        // Read file content
        string content = File.ReadAllText(filePath, Encoding.UTF8);

        // Process content line by line
        string[] contentLines = content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);

        // State tracking for multi-line block removal
        bool inSkippedBlock = false;
        int blockBraceDepth = 0;
        bool fileHasOutput = false;

        foreach (string line in contentLines)
        {
            // Handle multi-line block skipping (tracks nested braces)
            if (inSkippedBlock)
            {
                blockBraceDepth += line.Split('{').Length - 1;
                blockBraceDepth -= line.Split('}').Length - 1;
                if (blockBraceDepth <= 0) 
                { 
                    inSkippedBlock = false; 
                    blockBraceDepth = 0;
                    continue; // Don't output closing brace line that ended the block
                }
                continue; // Continue skipping lines inside the block
            }

            // Check if current line matches any removal pattern
            bool shouldRemoveLine = false;
            foreach (var patternEntry in patterns)
            {
                if (patternEntry.Value.IsMatch(line))
                {
                    // Check if this starts a multi-line block that needs brace tracking
                    if (blockStarters.Contains(patternEntry.Key))
                    {
                        Bump(patternEntry.Key);
                        
                        // Initialize brace tracking for this block
                        blockBraceDepth = line.Split('{').Length - line.Split('}').Length;
                        inSkippedBlock = true;
                        shouldRemoveLine = true;
                        break;
                    }
                    else
                    {
                        // Single-line removal
                        Bump(patternEntry.Key);
                        shouldRemoveLine = true;
                        break;
                    }
                }
            }

            if (!shouldRemoveLine)
            {
                // Skip pure whitespace lines to reduce output bloat
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Keep the line (trim trailing whitespace for consistency)
                output.AppendLine(line.TrimEnd());
                fileHasOutput = true;
            }
        }

        if (fileHasOutput)
        {
            filesWithContent++;
            output.AppendLine(); // Ensure separation between files
        }
    }

    // Squeeze excessive blank lines to maximum of one blank line
    string finalOutput = Regex.Replace(output.ToString(), @"(\r?\n){3,}", Environment.NewLine + Environment.NewLine);
    finalOutput = finalOutput.TrimEnd() + Environment.NewLine; // Ensure file ends with newline

    // Get output path via save dialog
    var parentDir = Directory.GetParent(modelFolder);
    string suggestedPath = Path.Combine(parentDir != null ? parentDir.FullName : modelFolder,
                                        Path.GetFileName(modelFolder) + ".slimdl");

    string outputPath;
    using (var saveDialog = new SaveFileDialog())
    {
        saveDialog.Title = "Save slimmed TMDL";
        saveDialog.Filter = "Slimmed TMDL (*.slimdl)|*.slimdl|TMDL files (*.tmdl)|*.tmdl|All files (*.*)|*.*";
        saveDialog.DefaultExt = "slimdl";
        saveDialog.AddExtension = true;
        saveDialog.FileName = Path.GetFileName(suggestedPath);
        saveDialog.InitialDirectory = Path.GetDirectoryName(suggestedPath);
        saveDialog.OverwritePrompt = true;
        saveDialog.CheckPathExists = true;
  
        if (saveDialog.ShowDialog() != DialogResult.OK) return;
        outputPath = saveDialog.FileName;
    }

    // Write the combined, slimmed TMDL
    File.WriteAllText(outputPath, finalOutput, new UTF8Encoding(false));

    // Calculate size reduction metrics
    long outputSize = new FileInfo(outputPath).Length;
    double reductionPercent = (originalTotalSize > 0) 
        ? (1.0 - (double)outputSize / (double)originalTotalSize) * 100.0 
        : 0.0;

    // Generate summary report
    var summary = new StringBuilder();
    summary.AppendLine("TMDL Slimmer Results");
    summary.AppendLine("====================");
    summary.AppendLine(string.Format("Files processed: {0} of {1}", filesWithContent, tmdlFiles.Length));
    if (culturesFilesSkipped > 0)
        summary.AppendLine(string.Format("Culture files not processed: {0}", culturesFilesSkipped));
    summary.AppendLine(string.Format("Input size:  {0:N1} KB", originalTotalSize / 1024.0));
    summary.AppendLine(string.Format("Output size: {0:N1} KB", outputSize / 1024.0));
    summary.AppendLine(string.Format("Size reduction: {0:F1}%", reductionPercent));

    if (removalStats.Count > 0)
    {
        int totalRemovals = 0;
        foreach (int count in removalStats.Values) totalRemovals += count;
        summary.AppendLine();
        if (culturesBytesRemoved > 0)
            summary.AppendLine(string.Format("Removed cultures folder: {0:N1} KB", culturesBytesRemoved / 1024.0));
        summary.AppendLine();
        summary.AppendLine(string.Format("Removed {0:N0} items:", totalRemovals));

        var sortedKeys = new List<string>(removalStats.Keys);
        sortedKeys.Sort();
        foreach (string key in sortedKeys)
            summary.AppendLine(string.Format("  - {0}: {1:N0}", key, removalStats[key]));
    }

    Info(summary.ToString());
}
catch (Exception ex)
{
    Error("Processing failed: " + ex.Message);
}
