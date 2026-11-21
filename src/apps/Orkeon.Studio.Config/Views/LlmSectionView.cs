using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Orkeon.Studio.Config.Presentation;
using Orkeon.Studio.Core.Llm;
using Orkeon.Studio.Core.Presets;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TerminalApp = Terminal.Gui.App.Application;

namespace Orkeon.Studio.Config.Views;

/// <summary>
/// The <c>Llm</c> screen. The provider line is read-only — an <c>appsettings.json</c> has no
/// provider key, the endpoint decides — and the API key fields carry the standing
/// recommendation to name the variable holding the key (<c>Llm:ApiKeyEnvVar</c>, STUDIO-49)
/// instead of writing the key into the file.
/// The "Test connection" button runs the optional connectivity probe of SPEC §4.2: it never
/// blocks the screen, and its verdict changes nothing about what can be saved. It presents the key
/// a run presents, and refuses without a request an endpoint that needs one when none resolves
/// (STUDIO-54). Below, the named profiles of <c>Llm:Profiles</c> and the one the RAG calls,
/// read-only (STUDIO-48). Every line of text takes one row, so none runs into the field under it: a
/// line that names something of the file (the provider read, a profile, the RAG's profile) is cut with
/// an ellipsis when it is longer than the row;
/// the status, last and under the button, is the one text that may take several: the notice of the
/// base URL's reading, whole, then the verdict of the connection test.
/// </summary>
internal sealed class LlmSectionView : SectionView
{
    private readonly LlmForm _form;
    private readonly ILlmEndpointProbe _probe;
    private readonly IEnvironmentVariables _environment;
    private readonly TextField _model;
    private readonly TextField _baseUrl;
    private readonly TextField _apiKey;
    private readonly TextField _apiKeyEnvVar;
    private readonly TextField _temperature;
    private readonly TextField _maxTokens;
    private readonly TextField _timeout;
    private readonly TextField _streamIdle;
    private readonly TextField _thinking;
    private readonly Label _provider;
    private readonly Label _notice;
    private readonly Label _apiKeyWarning;
    private readonly Button _testConnection;
    private readonly Label _status;
    private readonly ListView _profiles;
    private readonly Label _ragLlmProfile;

    /// <summary>The line the profile list shows when the file names no profile.</summary>
    internal const string NoProfileLine = "(none: every agent runs on the default)";

    /// <summary>Builds the screen over <paramref name="form"/>.</summary>
    /// <param name="form">The section's values, as text.</param>
    /// <param name="probe">Runs the optional connectivity test.</param>
    /// <param name="environment">Where the test reads the key a run reads; the machine's environment by default.</param>
    public LlmSectionView(LlmForm form, ILlmEndpointProbe probe, IEnvironmentVariables? environment = null)
        : base("LLM", scrolls: true)
    {
        _form = form ?? throw new ArgumentNullException(nameof(form));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _environment = environment ?? SystemEnvironmentVariables.Instance;

        _model = FormLayout.AddField(this, 0, "Model", _form.Model);
        _baseUrl = FormLayout.AddField(this, 1, "Base URL", _form.BaseUrl);
        _provider = FormLayout.AddChangingLine(this, 2, _form.ReadAsLine);
        _notice = FormLayout.AddChangingLine(this, 3, LlmForm.ReadingNotice);
        _apiKeyEnvVar = FormLayout.AddField(this, 4, "API key variable", _form.ApiKeyEnvVar);
        _apiKey = FormLayout.AddField(this, 5, "API key (stored in the file)", _form.ApiKey, secret: true);
        var y = 6;
        foreach (var line in LlmForm.ApiKeyRecommendationLines)
            FormLayout.AddLine(this, y++, line);
        _apiKeyWarning = FormLayout.AddChangingLine(this, y++, "");
        _temperature = FormLayout.AddField(this, y++, "Temperature", _form.Temperature);
        _maxTokens = FormLayout.AddField(this, y++, "Max tokens", _form.MaxTokens);
        _timeout = FormLayout.AddField(this, y++, "Timeout (seconds)", _form.TimeoutSeconds);
        _thinking = FormLayout.AddField(this, y++, "Thinking (true/false/blank)", _form.ThinkingEnabled);
        _streamIdle = FormLayout.AddField(this, y++, "Stream idle (s, blank: none)", _form.StreamIdleSeconds);
        FormLayout.AddLine(this, y++, LlmForm.ThinkingNotice);

        y++;
        foreach (var line in LlmForm.ProfilesNotice)
            FormLayout.AddLine(this, y++, line);
        ProfileLines = ProfileLinesOf(_form);
        // Under its caption, on the whole width: a profile's line is long, and cut with an ellipsis past it.
        _profiles = FormLayout.AddChoiceListBelow(this, y, 4, "Profiles in this file", ProfileLines, 0);
        y += 5;
        _ragLlmProfile = FormLayout.AddChangingLine(this, y, _form.RagLlmProfileLine);

        _testConnection = new Button { X = FormLayout.Margin, Y = y + 2, Text = "Test connection" };
        _testConnection.OnAccept(() => TestConnection());
        Add(_testConnection);
        // Last, with nothing under it: the one text of the screen that may take several rows.
        _status = FormLayout.AddText(this, y + 4, "");
        ShowReading();

        // The provider is inferred from the endpoint, so it follows every keystroke in it.
        _baseUrl.TextChanged += (_, _) =>
        {
            _form.BaseUrl = _baseUrl.Text ?? "";
            ShowReading();
        };

        _apiKey.TextChanged += (_, _) =>
        {
            _form.ApiKey = _apiKey.Text ?? "";
            RefreshApiKeyWarning();
        };

        RefreshApiKeyWarning();
    }

    /// <inheritdoc />
    public override void Load()
    {
        _model.Text = _form.Model;
        _baseUrl.Text = _form.BaseUrl;
        _apiKey.Text = _form.ApiKey;
        _apiKeyEnvVar.Text = _form.ApiKeyEnvVar;
        _temperature.Text = _form.Temperature;
        _maxTokens.Text = _form.MaxTokens;
        _timeout.Text = _form.TimeoutSeconds;
        _streamIdle.Text = _form.StreamIdleSeconds;
        _thinking.Text = _form.ThinkingEnabled;
        ShowReading();
        ProfileLines = ProfileLinesOf(_form);
        FormLayout.SetItems(_profiles, ProfileLines);
        FormLayout.SetLine(_ragLlmProfile, _form.RagLlmProfileLine);
        RefreshApiKeyWarning();
    }

    /// <summary>The lines the profile list shows, as last loaded.</summary>
    internal IReadOnlyList<string> ProfileLines { get; private set; }

    /// <summary>The RAG line as shown.</summary>
    internal string RagLlmProfileText => FormLayout.LineOf(_ragLlmProfile);

    private static IReadOnlyList<string> ProfileLinesOf(LlmForm form) =>
        form.Profiles.Count > 0 ? form.Profiles : [NoProfileLine];

    /// <inheritdoc />
    public override void Apply()
    {
        _form.Model = _model.Text ?? "";
        _form.BaseUrl = _baseUrl.Text ?? "";
        _form.ApiKey = _apiKey.Text ?? "";
        _form.ApiKeyEnvVar = _apiKeyEnvVar.Text ?? "";
        _form.Temperature = _temperature.Text ?? "";
        _form.MaxTokens = _maxTokens.Text ?? "";
        _form.TimeoutSeconds = _timeout.Text ?? "";
        _form.StreamIdleSeconds = _streamIdle.Text ?? "";
        _form.ThinkingEnabled = _thinking.Text ?? "";
    }

    /// <summary>
    /// The verdict of the last probe — the text the status carries under the notice. Set before the UI
    /// thread is asked to render it, so it is readable the moment the probe returns.
    /// </summary>
    internal string TestResult { get; private set; } = "";

    /// <summary>
    /// Starts a probe of whatever is currently on screen and returns immediately: the screen
    /// stays usable while the endpoint is being reached, and a failure is a message, never a
    /// reason to refuse anything else. An endpoint that needs a key when none resolves is not
    /// probed: a run would refuse it, and the line says so at once (STUDIO-54) — or names the two
    /// variables that set the key with different values, of which a run reads either (STUDIO-56).
    /// </summary>
    internal Task TestConnection()
    {
        // The user may have typed a new endpoint without leaving the screen, so read the
        // widgets back into the form before deciding what to probe.
        Apply();
        var key = _form.ResolveKey(_environment);
        var request = _form.ToProbeRequest(_environment);
        if (request.ApiKey is null && LlmPresets.NeedsApiKey(request.BaseUrl))
        {
            // Two spellings that disagree come first: setting one more key would not help (STUDIO-56).
            TestResult = key.IsConflict ? LlmForm.KeyConflictLine(key) : LlmForm.KeyMissingLine;
            ShowStatus();
            return Task.CompletedTask;
        }

        TestResult = "Testing the connection…";
        ShowStatus();
        return TestConnectionAsync(request);
    }

    [SuppressMessage("Design", "CA1031",
        Justification = "Fault barrier around a network probe started from a button: an " +
                        "unexpected failure belongs in the result label, not in an unobserved " +
                        "task exception that would tear the UI down.")]
    private async Task TestConnectionAsync(LlmProbeRequest request)
    {
        string message;
        try
        {
            var result = await _probe.ProbeAsync(request).ConfigureAwait(false);
            message = result.Message;
        }
        catch (Exception ex)
        {
            message = string.Create(CultureInfo.InvariantCulture, $"Connection failed: {ex.Message}");
        }

        TestResult = message;
        RenderTestResult();
    }

    /// <summary>
    /// Puts the verdict on the status. The probe answers on a background thread, so the update
    /// is marshalled onto the UI thread — except when no main loop is running (a screen built
    /// outside <c>Application.Init</c>, which is how these views are exercised headlessly),
    /// where <c>Invoke</c> would throw and there is no other thread to defer to anyway.
    /// </summary>
    private void RenderTestResult()
    {
        if (TerminalApp.Initialized)
            TerminalApp.Invoke(ShowStatus);
        else
            ShowStatus();
    }

    private void RefreshApiKeyWarning() =>
        FormLayout.SetLine(_apiKeyWarning, _form.StoresApiKeyInClearText ? LlmForm.ClearTextWarning : "");

    // The reading on its row; its notice, a sentence of several rows, whole in the status, and the
    // row under the reading says where to read it.
    private void ShowReading()
    {
        FormLayout.SetLine(_provider, _form.ReadAsLine);
        FormLayout.SetLine(_notice, _form.NoticeLine is null ? LlmForm.ReadingNotice : LlmForm.NoticeBelow);
        ShowStatus();
    }

    /// <summary>The status as shown: the notice of the base URL's reading, then the verdict of the last probe.</summary>
    internal string StatusText => _status.Text ?? "";

    private void ShowStatus() =>
        _status.Text = string.Join('\n', new[] { _form.NoticeLine, TestResult }.Where(line => !string.IsNullOrEmpty(line)));

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // The base View.Dispose also disposes the Add()-ed subviews; its IsDisposed
            // guard makes these explicit calls idempotent no-ops.
            _model.Dispose();
            _baseUrl.Dispose();
            _apiKey.Dispose();
            _apiKeyEnvVar.Dispose();
            _temperature.Dispose();
            _maxTokens.Dispose();
            _timeout.Dispose();
            _streamIdle.Dispose();
            _thinking.Dispose();
            _provider.Dispose();
            _notice.Dispose();
            _apiKeyWarning.Dispose();
            _testConnection.Dispose();
            _status.Dispose();
            _profiles.Dispose();
            _ragLlmProfile.Dispose();
        }

        base.Dispose(disposing);
    }
}
