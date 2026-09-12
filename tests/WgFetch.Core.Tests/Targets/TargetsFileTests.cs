using WgFetch.Core.Targets;
using WgFetch.Core.Tests.Support;
using YamlDotNet.RepresentationModel;

namespace WgFetch.Core.Tests.Targets;

public class TargetsFileTests
{
    private static string GoldenPath(string relative) =>
        Path.Combine(AppContext.BaseDirectory, "Golden", "targets", relative);

    [Fact]
    public void Render_CanonicalDocument_MatchesGoldenFile()
    {
        TargetsDocument doc = BuildCanonicalDocument();

        string rendered = TargetsFile.Render(doc);

        string expected = File.ReadAllText(GoldenPath("canonical.yaml"));
        Assert.Equal(Normalize(expected), Normalize(rendered));
    }

    [Fact]
    public void Parse_ThenRender_OfGoldenFile_IsByteIdentical()
    {
        // The documented shape in docs/REQUIREMENTS.md has every known field present explicitly
        // (including nulls). For a file already in that canonical shape, re-saving without changes
        // must be byte-identical.
        string golden = File.ReadAllText(GoldenPath("canonical.yaml"));

        TargetsDocument parsed = TargetsFile.Parse(golden);
        string rerendered = TargetsFile.Render(parsed);

        Assert.Equal(Normalize(golden), Normalize(rerendered));
    }

    [Fact]
    public void LoadSaveRoundTrip_IsIdempotent_ForCanonicalDocument()
    {
        TargetsDocument doc = BuildCanonicalDocument();

        string first = TargetsFile.Render(doc);
        TargetsDocument reloaded = TargetsFile.Parse(first);
        string second = TargetsFile.Render(reloaded);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Parse_UnknownEntryKeys_AreCapturedAndRoundTrip_ButAreAppendedAfterKnownFields()
    {
        // Documented actual behaviour: unknown keys are preserved verbatim, but this implementation
        // does not attempt to reconstruct their original interleaved position among known fields —
        // they are appended after every known field, in their original relative order.
        const string yaml = """
            version: 1
            targets:
              - name: nina
                id: AstroStack.NINA
                state: acquired
                futureField: hello
                anotherFuture:
                  nested: true
            """;

        TargetsDocument doc = TargetsFile.Parse(yaml);
        TargetEntry entry = Assert.Single(doc.Targets);

        Assert.Equal(2, entry.ExtraFields.Count);
        Assert.True(entry.ExtraFields.ContainsKey("futureField"));
        Assert.True(entry.ExtraFields.ContainsKey("anotherFuture"));
        Assert.Equal("hello", ((YamlScalarNode)entry.ExtraFields["futureField"]).Value);

        string rendered = TargetsFile.Render(doc);
        int futureFieldIndex = rendered.IndexOf("futureField: hello", StringComparison.Ordinal);
        int lastErrorIndex = rendered.IndexOf("lastError: null", StringComparison.Ordinal);
        Assert.True(futureFieldIndex > lastErrorIndex, "unknown keys must be appended after known fields");

        // Round-tripping again must not lose or duplicate the unknown keys.
        TargetsDocument reparsed = TargetsFile.Parse(rendered);
        Assert.Equal(2, reparsed.Targets[0].ExtraFields.Count);
    }

    [Fact]
    public void Parse_UnknownTopLevelKeys_ArePreserved()
    {
        const string yaml = """
            version: 1
            targets: []
            futureTopLevel: value
            """;

        TargetsDocument doc = TargetsFile.Parse(yaml);

        Assert.True(doc.ExtraFields.ContainsKey("futureTopLevel"));

        string rendered = TargetsFile.Render(doc);
        Assert.Contains("futureTopLevel: value", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_NonScalarTopLevelKey_FailsClosedWithFormatException()
    {
        const string yaml = """
            ? [invalid]
            : value
            targets: []
            """;

        var ex = Assert.Throws<FormatException>(() => TargetsFile.Parse(yaml));
        Assert.Contains("mapping keys must be scalar", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_NonScalarEntryKey_FailsClosedWithFormatException()
    {
        const string yaml = """
            version: 1
            targets:
              - name: nina
                ? [invalid]
                : value
            """;

        var ex = Assert.Throws<FormatException>(() => TargetsFile.Parse(yaml));
        Assert.Contains("mapping keys must be scalar", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_OversizedVersionScalar_FailsClosedWithFormatException()
    {
        const string yaml = """
            version: 99999999999999999999
            targets: []
            """;

        var ex = Assert.Throws<FormatException>(() => TargetsFile.Parse(yaml));
        Assert.Contains("version", ex.Message, StringComparison.Ordinal);
        Assert.IsType<OverflowException>(ex.InnerException);
    }

    [Fact]
    public void Parse_NonNumericVersionScalar_FailsClosedWithFormatException()
    {
        const string yaml = """
            version: not-a-number
            targets: []
            """;

        var ex = Assert.Throws<FormatException>(() => TargetsFile.Parse(yaml));
        Assert.Contains("version", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("version: [1]\ntargets: []")]
    [InlineData("version:\n  value: 1\ntargets: []")]
    public void Parse_NonScalarVersion_FailsClosedWithFormatException(string yaml)
    {
        var ex = Assert.Throws<FormatException>(() => TargetsFile.Parse(yaml));

        Assert.Contains("version must be a scalar", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("targets: [\n")]
    [InlineData("version: 1\nversion: 2\n")]
    [InlineData("targets:\n  - name: nina\n    name: phd2\n")]
    [InlineData("? [a, b]\n: 1\n? [a, b]\n: 2\n")]
    [InlineData("*missing\n")]
    public void Parse_MalformedYaml_FailsClosedWithTargetsFileException(string yaml)
    {
        var ex = Assert.Throws<TargetsFileException>(() => TargetsFile.Parse(yaml));

        Assert.Contains("not valid YAML", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_MalformedYaml_FailsClosedWithPathBearingException()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "targets.yaml");
        await File.WriteAllTextAsync(path, "version: 1\nversion: 2\n", CancellationToken.None);

        var ex = await Assert.ThrowsAsync<TargetsFileException>(() => TargetsFile.LoadAsync(path));

        Assert.Equal(path, ex.FilePath);
        Assert.Contains("unreadable or malformed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_SchemaInvalidYaml_FailsClosedWithTargetsFileException()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "targets.yaml");
        await File.WriteAllTextAsync(path, "version: not-a-number\ntargets: []\n", CancellationToken.None);

        var ex = await Assert.ThrowsAsync<TargetsFileException>(() => TargetsFile.LoadAsync(path));

        Assert.Equal(path, ex.FilePath);
        Assert.IsType<FormatException>(ex.InnerException);
    }

    [Fact]
    public void Add_IsIdempotent_AndCaseInsensitive()
    {
        var doc = new TargetsDocument();

        TargetEntry first = doc.Add("nina");
        TargetEntry second = doc.Add("NINA");

        Assert.Same(first, second);
        Assert.Single(doc.Targets);
    }

    [Fact]
    public void Add_NewName_DefaultsToListedState()
    {
        var doc = new TargetsDocument();

        TargetEntry entry = doc.Add("phd2");

        Assert.Equal(TargetState.Listed, entry.State);
        Assert.Empty(entry.Allowlist);
    }

    [Fact]
    public void Remove_IsCaseInsensitive()
    {
        var doc = new TargetsDocument();
        doc.Add("nina");

        bool removed = doc.Remove("NINA");

        Assert.True(removed);
        Assert.Empty(doc.Targets);
    }

    [Fact]
    public void Find_ReturnsNull_WhenAbsent()
    {
        var doc = new TargetsDocument();
        Assert.Null(doc.Find("nina"));
    }

    [Theory]
    [InlineData(TargetState.Listed, true)]
    [InlineData(TargetState.Resolved, true)]
    [InlineData(TargetState.Stale, true)]
    [InlineData(TargetState.Acquired, false)]
    [InlineData(TargetState.Blocked, false)]
    public void EntriesNeedingAcquisition_DefaultSemantics_MatchesListedResolvedStale(TargetState state, bool expected)
    {
        var doc = new TargetsDocument();
        doc.Add("x").State = state;

        IReadOnlyList<TargetEntry> result = doc.EntriesNeedingAcquisition();

        Assert.Equal(expected, result.Count == 1);
    }

    [Fact]
    public void EntriesNeedingAcquisition_OnlyMissing_ExcludesStale()
    {
        var doc = new TargetsDocument();
        doc.Add("listed-app").State = TargetState.Listed;
        doc.Add("resolved-app").State = TargetState.Resolved;
        doc.Add("stale-app").State = TargetState.Stale;

        IReadOnlyList<TargetEntry> result = doc.EntriesNeedingAcquisition(onlyMissing: true);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, t => t.State == TargetState.Stale);
    }

    [Fact]
    public void EntriesNeedingAcquisition_RefreshStale_OnlyAcquiredOrStale()
    {
        var doc = new TargetsDocument();
        doc.Add("listed-app").State = TargetState.Listed;
        doc.Add("acquired-app").State = TargetState.Acquired;
        doc.Add("stale-app").State = TargetState.Stale;
        doc.Add("blocked-app").State = TargetState.Blocked;

        IReadOnlyList<TargetEntry> result = doc.EntriesNeedingAcquisition(refreshStale: true);

        Assert.Equal(2, result.Count);
        Assert.All(result, t => Assert.True(t.State is TargetState.Acquired or TargetState.Stale));
    }

    [Fact]
    public async Task LoadAsync_MissingFile_ReturnsEmptyDocument_NotAnError()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            $"does-not-exist-{Guid.NewGuid():N}.yaml");

        TargetsDocument doc = await TargetsFile.LoadAsync(path);

        Assert.Equal(TargetsDocument.CurrentSchemaVersion, doc.Version);
        Assert.Empty(doc.Targets);
    }

    [Fact]
    public async Task LoadAsync_MissingParentDirectory_ReturnsEmptyDocument_NotAnError()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            $"does-not-exist-{Guid.NewGuid():N}",
            "targets.yaml");

        TargetsDocument doc = await TargetsFile.LoadAsync(path);

        Assert.Equal(TargetsDocument.CurrentSchemaVersion, doc.Version);
        Assert.Empty(doc.Targets);
    }

    [Fact]
    public async Task LoadAsync_NonRegularPath_FailsClosedWithoutBlocking()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "targets.yaml");
        Directory.CreateDirectory(path);

        var loadTask = TargetsFile.LoadAsync(path);
        var completed = await Task.WhenAny(loadTask, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(loadTask, completed);

        var ex = await Assert.ThrowsAsync<TargetsFileException>(() => loadTask);
        Assert.Contains("unreadable or malformed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_LinuxFifo_FailsClosedWithoutBlocking()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "targets.yaml");
        if (!await SpecialFiles.TryCreateFifoAsync(path))
        {
            return;
        }

        // No writer ever opens this FIFO: the loader must return on its own rather than wait for one.
        var loadTask = TargetsFile.LoadAsync(path);
        var completed = await Task.WhenAny(loadTask, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(loadTask, completed);
        var ex = await Assert.ThrowsAsync<TargetsFileException>(() => loadTask);
        Assert.Contains("not a regular file", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_LinuxCharacterDevice_FailsClosedWithoutReadingEndlessly()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/dev/zero"))
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        var loadTask = TargetsFile.LoadAsync("/dev/zero", cancellation.Token);
        var completed = await Task.WhenAny(loadTask, Task.Delay(TimeSpan.FromSeconds(5)));
        if (!ReferenceEquals(completed, loadTask))
        {
            // Never leave an unbounded read of /dev/zero running behind a failing assertion.
            await cancellation.CancelAsync();
        }

        Assert.Same(loadTask, completed);
        var ex = await Assert.ThrowsAsync<TargetsFileException>(() => loadTask);

        // With statx the type check rejects the device outright; without it the read is still bounded.
        Assert.True(
            ex.Message.Contains("not a regular file", StringComparison.Ordinal) ||
            ex.Message.Contains("byte limit", StringComparison.Ordinal),
            ex.Message);
    }

    [Fact]
    public async Task LoadAsync_LinuxNullDevice_FailsClosedInsteadOfParsingItAsAnEmptyDocument()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/dev/null"))
        {
            return;
        }

        var ex = await Assert.ThrowsAsync<TargetsFileException>(() => TargetsFile.LoadAsync("/dev/null"));

        Assert.Contains("not a regular file", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_WindowsNullDevice_FailsClosedInsteadOfParsingItAsAnEmptyDocument()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var ex = await Assert.ThrowsAsync<TargetsFileException>(() => TargetsFile.LoadAsync("NUL"));

        Assert.Contains("not a regular file", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTrips_AndLeavesNoTempFile()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, $"targets-save-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "targets.yaml");

        try
        {
            TargetsDocument doc = BuildCanonicalDocument();

            await TargetsFile.SaveAsync(doc, path);

            Assert.True(File.Exists(path));
            string[] entries = Directory.GetFiles(dir);
            Assert.Single(entries); // only targets.yaml; no leftover .tmp file

            TargetsDocument reloaded = await TargetsFile.LoadAsync(path);
            Assert.Equal(TargetsFile.Render(doc), TargetsFile.Render(reloaded));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_Overwrites_ExistingFile()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, $"targets-overwrite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "targets.yaml");

        try
        {
            var first = new TargetsDocument();
            first.Add("nina");
            await TargetsFile.SaveAsync(first, path);

            var second = new TargetsDocument();
            second.Add("phd2");
            await TargetsFile.SaveAsync(second, path);

            TargetsDocument reloaded = await TargetsFile.LoadAsync(path);
            Assert.Null(reloaded.Find("nina"));
            Assert.NotNull(reloaded.Find("phd2"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PopulatedButUnacquiredDocument_IsValid_NeverThrows()
    {
        var doc = new TargetsDocument();
        doc.Add("nina");
        doc.Add("phd2");
        doc.Find("phd2")!.State = TargetState.Blocked;

        // Merely rendering/parsing a wishlist with nothing acquired must never throw.
        string rendered = TargetsFile.Render(doc);
        TargetsDocument reparsed = TargetsFile.Parse(rendered);

        Assert.Equal(2, reparsed.Targets.Count);
        Assert.All(reparsed.Targets, t => Assert.NotEqual(TargetState.Acquired, t.State));
    }

    private static TargetsDocument BuildCanonicalDocument()
    {
        var doc = new TargetsDocument();

        TargetEntry nina = doc.Add("nina");
        nina.Id = "AstroStack.NINA";
        nina.ComponentId = "nina";
        nina.State = TargetState.Acquired;
        nina.AcquiredVersion = "3.2.0.9001";
        nina.AvailableVersion = "3.2.0.9001";
        nina.Arch = "x64";
        nina.Scope = "machine";
        nina.Allowlist.Add("nighttime-imaging.eu");
        nina.Allowlist.Add("github.com");
        nina.LastAttempt = new DateTimeOffset(2026, 9, 10, 14, 22, 11, TimeSpan.Zero);

        TargetEntry sharpcap = doc.Add("sharpcap");
        sharpcap.ComponentId = "sharpcap";

        return doc;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd('\n');
}
