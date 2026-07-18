/*
 * Copyright (c) 2025-2026 Aegis AO Soft LLC and Alexander Orlov.
 * 34 Middletown Ave, Atlantic Highlands, NJ 07716
 *
 * THIS SOFTWARE IS THE CONFIDENTIAL AND PROPRIETARY INFORMATION OF
 * Aegis AO Soft LLC and Alexander Orlov.
 *
 * This code may be used, reproduced, modified, or distributed ONLY with the
 * prior written permission of Aegis AO Soft LLC / Alexander Orlov.
 *
 * Author: Alexander Orlov
 * Aegis AO Soft LLC
 */

using System.Text;

namespace Aegis.Localizer.Io;

/// <summary>
/// Reading and writing the user's source files without changing anything we were not asked to.
///
/// The default File.ReadAllText decodes invalid UTF-8 to U+FFFD and writing that back replaces
/// every such byte with EF BF BD - a Latin-1 file with one accented character in a comment would
/// come out permanently mangled, and because the scanner and the rewriter mangle identically, the
/// span check would not notice. So decoding is strict here: a file we cannot read losslessly is
/// reported and skipped rather than rewritten.
/// </summary>
public static class SourceFile
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public sealed record Content(string Text, bool HasBom);

    /// <summary>Reads a file as UTF-8, returning null when it is not valid UTF-8 or cannot be read.</summary>
    public static Content? TryRead(string path)
    {
        byte[] bytes;

        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

        try
        {
            var text = StrictUtf8.GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
            return new Content(text, hasBom);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes text back, preserving whether the file had a byte-order mark. The write goes to a
    /// temporary file first and is then moved into place, so an interrupted run cannot leave a
    /// half-written source file behind.
    /// </summary>
    public static void Write(string path, string text, bool hasBom)
    {
        var temp = path + ".aegis-tmp";

        try
        {
            File.WriteAllText(temp, text, hasBom ? Utf8WithBom : Utf8NoBom);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }
    }
}
