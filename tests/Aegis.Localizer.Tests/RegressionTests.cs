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
using Aegis.Localizer.Emit;
using Aegis.Localizer.Resources;
using Xunit;

namespace Aegis.Localizer.Tests;

/// <summary>
/// One test per defect found while reviewing the core. Each names the damage it prevents, because
/// this tool edits other people's code and every one of these was a way to do that badly.
/// </summary>
public class RegressionTests
{
    private const string Source =
        """
        namespace Demo;

        public class Screen
        {
            public string Title => "Save changes";
        }
        """;

    private static LocalizationRequest Request(string path, bool apply = false) => new()
    {
        ProjectPath = path,
        Languages = ["ru"],
        Apply = apply,
        UseCache = false
    };

    /// <summary>
    /// A Latin-1 file decoded as UTF-8 turns every accented byte into U+FFFD, and writing it back
    /// makes that permanent. The scanner and rewriter mangle identically, so the span check cannot
    /// notice. Such a file must be left alone entirely.
    /// </summary>
    [Fact]
    public async Task FilesThatAreNotUtf8AreLeftUntouched()
    {
        using var temp = new TempFolder();
        var file = Path.Combine(temp.Path, "Screen.cs");

        // "café" in Windows-1252: the 0xE9 byte is not valid UTF-8.
        var bytes = Encoding.Latin1.GetBytes("// café\r\n" + Source);
        await File.WriteAllBytesAsync(file, bytes);

        var result = await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path, apply: true));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
        Assert.Empty(result.Candidates);
    }

    /// <summary>A backup exists to undo us, so it has to be the original bytes, not a re-encoding.</summary>
    [Fact]
    public async Task BackupsAreByteIdenticalToTheOriginal()
    {
        using var temp = new TempFolder();
        var file = Path.Combine(temp.Path, "Screen.cs");

        var original = new UTF8Encoding(true).GetBytes(Source);
        await File.WriteAllBytesAsync(file, original);

        var result = await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path, apply: true));

        var backup = Path.Combine(result.Rewrite!.BackupDirectory, "Screen.cs");
        Assert.Equal(original, await File.ReadAllBytesAsync(backup));
    }

    /// <summary>A byte-order mark is part of the file; dropping it dirties the diff of every file we touch.</summary>
    [Fact]
    public async Task ByteOrderMarksSurviveARewrite()
    {
        using var temp = new TempFolder();
        var file = Path.Combine(temp.Path, "Screen.cs");
        await File.WriteAllTextAsync(file, Source, new UTF8Encoding(true));

        await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path, apply: true));

        var bytes = await File.ReadAllBytesAsync(file);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
    }

    /// <summary>
    /// Two keys holding the same copy is ordinary in a real bundle. Inverting key->value into
    /// value->key without handling that threw, after the classification pass had already been paid for.
    /// </summary>
    [Fact]
    public async Task ADuplicateValueInAnExistingBundleDoesNotAbortTheRun()
    {
        using var temp = new TempFolder();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "Screen.cs"), Source);

        var store = new ResxStore();
        var location = new ResourceLocation(Path.Combine(temp.Path, "Localization"), "AppResources", null, "en");
        store.Write(location, new Dictionary<string, string> { ["OkButton"] = "OK", ["ConfirmButton"] = "OK" });

        var result = await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path));

        Assert.NotEmpty(result.Localized);
    }

    /// <summary>
    /// A real i18next bundle is full of things this tool does not model. Rebuilding the file from
    /// our flat view of it deleted every nested namespace and plural group.
    /// </summary>
    [Fact]
    public void NestedAndNonStringEntriesSurviveAMerge()
    {
        using var temp = new TempFolder();
        var store = new I18NextJsonStore();
        var location = new ResourceLocation(temp.Path, "translation", "en", "en");

        Directory.CreateDirectory(Path.Combine(temp.Path, "en"));
        File.WriteAllText(
            store.ResolvePath(location),
            """
            {
              "home": { "title": "Hi" },
              "count_other": "{{count}} items",
              "limits": [1, 2, 3]
            }
            """);

        store.Write(location, new Dictionary<string, string> { ["NewKey"] = "New copy" });

        var json = File.ReadAllText(store.ResolvePath(location));

        Assert.Contains("\"home\"", json);
        Assert.Contains("\"title\": \"Hi\"", json);
        Assert.Contains("\"count_other\"", json);
        Assert.Contains("\"limits\"", json);
        Assert.Contains("\"NewKey\"", json);
    }

    /// <summary>A malformed resource file is a merge conflict, not an invitation to overwrite.</summary>
    [Fact]
    public void AMalformedResxIsRefusedRatherThanReplaced()
    {
        using var temp = new TempFolder();
        var store = new ResxStore();
        var location = new ResourceLocation(temp.Path, "AppResources", "fr", "en");

        const string broken = "<root><data name=\"A\"><value>Bonjour</value></data>";
        File.WriteAllText(store.ResolvePath(location), broken);

        Assert.Throws<InvalidOperationException>(() =>
            store.Write(location, new Dictionary<string, string> { ["B"] = "Salut" }));

        Assert.Equal(broken, File.ReadAllText(store.ResolvePath(location)));
    }

    /// <summary>
    /// The bundle can hold keys this tool never minted. Emitting them as properties produced an
    /// accessor that does not compile, breaking the user's build on a run that reported success.
    /// </summary>
    [Fact]
    public void TheAccessorSkipsKeysThatAreNotLegalMemberNames()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "L.cs");

        AccessorWriter.Write(path, "Demo", "L", "Demo.AppResources",
            ["Good", "Error.NotFound", "My Key", "2FA", "Format", "Get"]);

        var code = File.ReadAllText(path);

        Assert.Contains("public static string Good =>", code);
        Assert.DoesNotContain("Error.NotFound =>", code);
        Assert.DoesNotContain("My Key", code);
        Assert.DoesNotContain("2FA =>", code);

        // Format and Get already exist on the class; a property would be a duplicate member.
        Assert.Equal(1, Occurrences(code, "public static string Format"));
        Assert.Equal(1, Occurrences(code, "public static string Get"));
    }

    /// <summary>
    /// Adding --context changes what the model is asked. Keying the cache on the text alone
    /// returned the old answers and the flag silently did nothing.
    /// </summary>
    [Fact]
    public async Task ChangingTheProjectContextInvalidatesCachedAnswers()
    {
        using var temp = new TempFolder();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "Screen.cs"), Source);

        var model = new FakeModel();

        await new LocalizationRunner(model).RunAsync(new LocalizationRequest
        {
            ProjectPath = temp.Path, Languages = ["ru"], ProjectContext = "a banking app"
        });

        var afterFirst = model.Calls;

        await new LocalizationRunner(model).RunAsync(new LocalizationRequest
        {
            ProjectPath = temp.Path, Languages = ["ru"], ProjectContext = "a children's game"
        });

        Assert.True(model.Calls > afterFirst, "the second run reused answers produced for a different prompt");
    }

    /// <summary>
    /// When a translation is rejected the source string is used instead. Caching that fallback
    /// memoized English as the translation for ever, so the bad response could never be retried.
    /// </summary>
    [Fact]
    public async Task ARejectedTranslationIsNotCachedAsIfItWereOne()
    {
        using var temp = new TempFolder();
        await File.WriteAllTextAsync(
            Path.Combine(temp.Path, "Screen.cs"),
            """
            namespace Demo;

            public class Screen
            {
                public string Greet(string name) => $"Hello {name}";
            }
            """);

        var request = new LocalizationRequest { ProjectPath = temp.Path, Languages = ["ru"] };

        // First run drops the placeholder, so the translation is refused.
        var broken = new FakeModel { Translate = (_, text) => text.Replace("{0}", "there") };
        await new LocalizationRunner(broken).RunAsync(request);

        // Second run behaves; the refused answer must not have been remembered.
        var healthy = new FakeModel();
        var result = await new LocalizationRunner(healthy).RunAsync(request);

        var entry = result.Localized.First(e => e.Candidate.Text.Contains("{0}"));
        Assert.StartsWith("[ru]", entry.Translations["ru"]);
    }

    /// <summary>
    /// The import went in by rebuilding the file from split lines, which normalised every line
    /// ending: a one-line change came back as a whole-file diff.
    /// </summary>
    [Fact]
    public async Task RewritingPreservesTheFilesOwnLineEndings()
    {
        using var temp = new TempFolder();
        var file = Path.Combine(temp.Path, "Screen.cs");
        await File.WriteAllTextAsync(file, Source.Replace("\r\n", "\n").Replace("\n", "\r\n"));

        await new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path, apply: true));

        var text = await File.ReadAllTextAsync(file);
        Assert.False(HasLoneLf(text), "a CRLF file came back with bare LF line endings");
    }

    /// <summary>
    /// A directory symlink pointing back at an ancestor is not exotic - repositories really contain
    /// them - and a recursive enumerator descends one forever. The scan has to finish.
    /// </summary>
    [Fact]
    public async Task ASymlinkLoopDoesNotHangTheScan()
    {
        using var temp = new TempFolder();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "Screen.cs"), Source);

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(temp.Path, "loop"), temp.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // Creating links needs privileges this machine may not grant.
        }

        var run = new LocalizationRunner(new FakeModel()).RunAsync(Request(temp.Path));
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.True(ReferenceEquals(finished, run), "the scan did not finish: it followed the symlink loop");
        Assert.NotEmpty((await run).Localized);
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static bool HasLoneLf(string text)
    {
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n' && (i == 0 || text[i - 1] != '\r'))
                return true;

        return false;
    }
}
