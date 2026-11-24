using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Orkeon.Studio.Core.Chat;
using Orkeon.Studio.Core.Configuration;
using Orkeon.Studio.Core.Email;
using Orkeon.Studio.Core.Localization;
using Orkeon.Studio.Core.Validation;
using Orkeon.Studio.Wpf.ViewModels.Common;
using Orkeon.Studio.Wpf.ViewModels.Mvvm;

namespace Orkeon.Studio.Wpf.ViewModels.Config;

/// <summary>
/// One chat account of the Microsoft Teams tab, on the model of <see cref="EmailAccountRowViewModel"/>:
/// every key of the account as a field — text for a value, a switch for each of the six rights and
/// of the four administrator grants —, and what the run will say of the account as the file holds
/// it (<see cref="Problems"/>). Every edit writes the whole account in place; a field emptied
/// removes its key, and a value the user did not touch keeps the spelling the file had. A field the
/// kind of the account has no use for shows only while the file holds it.
/// <para>
/// The secret of the account — the address of a webhook, the client secret of an application — is
/// typed here and never written: it goes to the key store under a variable — the one the file
/// names, else one derived from the account's name (<see cref="ChatSecretNames"/>), which the first
/// value kept writes into the file.
/// </para>
/// <para>
/// Whether the account is ready, and whether it connects, is the engine's to say: <see cref="State"/>
/// is its answer for the file as saved, <see cref="TestCommand"/> its connection check. An account a
/// person signs in has <see cref="SignInCommand"/>, whose panel follows <c>orkeon chat login</c>, and
/// <see cref="SignOutCommand"/>, which asks first, then runs <c>orkeon chat logout</c>; a webhook and
/// an application have neither.
/// </para>
/// </summary>
public sealed class ChatAccountRowViewModel : ObservableObject
{
    private readonly ChatSectionViewModel _owner;
    private readonly IStudioStrings _strings;
    private ChatAccountDefinition _loaded;
    private IReadOnlyList<ValidationMessageViewModel> _problems = [];
    private string _name;
    private string? _provider;
    private ChatRights _rights;
    private string? _rightsRaw;
    private string? _authMethod;
    private string _clientId;
    private string _tenant;
    private ImmutableList<string> _adminGranted;
    private string _certificatePathEnvVar;
    private string _federatedTokenFileEnvVar;
    private string _clientSecretEnvVar;
    private string _webhookEnvVar;
    private string? _cloud;
    private string _maxPerHour;
    private int? _maxPerHourNumber;
    private bool _isRenaming;
    private string _activeTab = AccountTab;
    private bool _isConfirmingRemove;
    private string _renameText = "";
    private SecretRowViewModel? _secret;
    private bool _secretKept;
    private bool _secretIsWebhook;
    private ChatAccountState? _state;
    private EmailCliFailure? _stateFailure;
    private EmailCheckOutcome? _lastCheck;
    private bool _isTesting;
    private CancellationTokenSource? _test;
    private EmailSignInViewModel? _signIn;
    private bool _isConfirmingSignOut;
    private string? _signOutFailure;

    internal ChatAccountRowViewModel(ChatAccountDefinition account, ChatSectionViewModel owner, IStudioStrings strings)
    {
        _owner = owner;
        _strings = strings;
        _loaded = account;
        _name = account.Name;
        _provider = account.Provider;
        _rights = account.Rights;
        _rightsRaw = account.RightsRaw;
        _authMethod = account.AuthMethod;
        _clientId = account.ClientId ?? "";
        _tenant = account.Tenant ?? "";
        _adminGranted = account.AdminGranted;
        _certificatePathEnvVar = account.CertificatePathEnvVar ?? "";
        _federatedTokenFileEnvVar = account.FederatedTokenFileEnvVar ?? "";
        _clientSecretEnvVar = account.ClientSecretEnvVar ?? "";
        _webhookEnvVar = account.WebhookEnvVar ?? "";
        _cloud = account.Cloud;
        _maxPerHourNumber = account.MaxPerHour;
        _maxPerHour = NumberText(account.MaxPerHour);
        foreach (var place in account.ReadPlaces)
            ReadPlaces.Add(new ChatPlaceRowViewModel(this, place));
        foreach (var place in account.SendPlaces)
            SendPlaces.Add(new ChatPlaceRowViewModel(this, place));

        AddReadPlaceCommand = new RelayCommand(() => ReadPlaces.Add(new ChatPlaceRowViewModel(this, "")));
        AddSendPlaceCommand = new RelayCommand(() => SendPlaces.Add(new ChatPlaceRowViewModel(this, "")));
        RemovePlaceCommand = new RelayCommand(
            parameter => { if (parameter is ChatPlaceRowViewModel line) RemovePlace(line); },
            parameter => parameter is ChatPlaceRowViewModel);
        ConfirmRenameCommand = new RelayCommand(ConfirmRename, () => _isRenaming && RenameRefusal is null);
        CancelRenameCommand = new RelayCommand(() => IsRenaming = false);
        ConfirmRemoveCommand = new RelayCommand(() => { if (_isConfirmingRemove) _owner.Remove(this); });
        CancelRemoveCommand = new RelayCommand(() => IsConfirmingRemove = false);
        TestCommand = new AsyncRelayCommand(TestAsync, () => _owner.CanTest);
        SignInCommand = new RelayCommand(BeginSignIn, () => CanSign && _signIn is not { IsWaiting: true });
        SignOutCommand = new RelayCommand(() => { if (CanSign) _owner.ArmSignOut(this); }, () => CanSign);
        ConfirmSignOutCommand = new AsyncRelayCommand(SignOutAsync, () => _isConfirmingSignOut);
        CancelSignOutCommand = new RelayCommand(() => IsConfirmingSignOut = false);
        ShowAccountTabCommand = new RelayCommand(() => ActiveTab = AccountTab);
        ShowRightsTabCommand = new RelayCommand(() => ActiveTab = RightsTab);
        ShowAuthTabCommand = new RelayCommand(() => ActiveTab = AuthTab);
        RefreshProblems();
    }

    // ── the three tabs of the form ──

    /// <summary>The tab of the account itself: its service and its cap of writes.</summary>
    public const string AccountTab = "account";

    /// <summary>The tab of what an agent may do: the six rights, and the places each reading and each writing is confined to.</summary>
    public const string RightsTab = "rights";

    /// <summary>The tab of the sign-in: who signs in, the application and its tenant, the grants, the secret.</summary>
    public const string AuthTab = "auth";

    /// <summary>The showing tab of the form; the head of the account stays above whichever shows.</summary>
    public string ActiveTab
    {
        get => _activeTab;
        set
        {
            var requested = value is RightsTab or AuthTab ? value : AccountTab;
            if (SetProperty(ref _activeTab, requested))
            {
                OnPropertiesChanged(nameof(IsAccountTab), nameof(IsRightsTab), nameof(IsAuthTab));
                _owner.TabMoved(this);
            }
        }
    }

    /// <summary>True while the account tab shows.</summary>
    public bool IsAccountTab => _activeTab == AccountTab;

    /// <summary>True while the rights tab shows.</summary>
    public bool IsRightsTab => _activeTab == RightsTab;

    /// <summary>True while the sign-in tab shows.</summary>
    public bool IsAuthTab => _activeTab == AuthTab;

    /// <summary>Shows the account tab.</summary>
    public RelayCommand ShowAccountTabCommand { get; }

    /// <summary>Shows the rights tab.</summary>
    public RelayCommand ShowRightsTabCommand { get; }

    /// <summary>Shows the sign-in tab.</summary>
    public RelayCommand ShowAuthTabCommand { get; }

    /// <summary>The name the file holds the account under: what an agent passes as <c>account</c>.</summary>
    public string Name => _name;

    /// <summary>
    /// The service, in the engine's spelling; a text the engine does not know shows as written (no
    /// entry of the list then matches it), and null when the file names none.
    /// </summary>
    public string? Provider
    {
        get => Shown(ChatSection.Providers, _provider);
        set
        {
            var chosen = string.IsNullOrWhiteSpace(value) ? null : value;
            if (string.Equals(Provider, chosen, StringComparison.Ordinal))
                return;

            _provider = chosen;
            OnPropertyChanged();
            Commit();
        }
    }

    /// <summary>What the list shows beside the service: the rights the account grants, or the text the file holds for them.</summary>
    public string RightsLine => _rights == ChatRights.None
        ? _rightsRaw ?? ""
        : string.Join(", ", Enum.GetValues<ChatRights>().Where(right => right != ChatRights.None && Has(right)));

    /// <summary><c>Send:MaxPerHour</c>, as typed; empty sets no cap.</summary>
    public string MaxPerHour
    {
        get => _maxPerHour;
        set => SetNumber(ref _maxPerHour, ref _maxPerHourNumber, value);
    }

    /// <summary>
    /// Whether the number field holds something that is no whole number. Such a text stays on
    /// screen and is not written: the document keeps the last number the field held.
    /// </summary>
    public bool HasUnreadNumber => Unread(_maxPerHour);

    // ── the six rights ──

    /// <summary>List the places, read and search the messages, list the members.</summary>
    public bool CanRead
    {
        get => Has(ChatRights.Read);
        set => SetRight(ChatRights.Read, value);
    }

    /// <summary>Post messages and replies.</summary>
    public bool CanSend
    {
        get => Has(ChatRights.Send);
        set => SetRight(ChatRights.Send, value);
    }

    /// <summary>Change the account's own messages.</summary>
    public bool CanEdit
    {
        get => Has(ChatRights.Edit);
        set => SetRight(ChatRights.Edit, value);
    }

    /// <summary>Delete the account's own messages.</summary>
    public bool CanDelete
    {
        get => Has(ChatRights.Delete);
        set => SetRight(ChatRights.Delete, value);
    }

    /// <summary>Add and remove reactions.</summary>
    public bool CanReact
    {
        get => Has(ChatRights.React);
        set => SetRight(ChatRights.React, value);
    }

    /// <summary>List meetings and read their transcripts.</summary>
    public bool CanMeetings
    {
        get => Has(ChatRights.Meetings);
        set => SetRight(ChatRights.Meetings, value);
    }

    // A right the kind of the account cannot hold is not offered; one the file grants anyway
    // shows, so that it can be turned off.

    /// <summary>Whether the reading right shows: never for a webhook, which only posts.</summary>
    public bool ShowRead => !IsWebhook || CanRead;

    /// <summary>
    /// Whether the sending right shows: never for an application, which only reads, nor for a
    /// webhook, whose right is said in its own sentence (<see cref="IsWebhook"/>).
    /// </summary>
    public bool ShowSend => !IsWebhook && (!IsApplication || CanSend);

    /// <summary>Whether the editing right shows: to an account a person signs in.</summary>
    public bool ShowEdit => IsPerson || CanEdit;

    /// <summary>Whether the deleting right shows: to an account a person signs in.</summary>
    public bool ShowDelete => IsPerson || CanDelete;

    /// <summary>Whether the reacting right shows: to an account a person signs in.</summary>
    public bool ShowReact => IsPerson || CanReact;

    /// <summary>Whether the meetings right shows: never for a webhook.</summary>
    public bool ShowMeetings => !IsWebhook || CanMeetings;

    /// <summary>Whether the account is a webhook, whose sending right is said in its own sentence.</summary>
    public bool IsWebhook => ChatAccountRules.ProviderOf(_provider) == ChatSection.Values.TeamsWebhook;

    // ── the places ──

    /// <summary><c>Read:AllowedPlaces</c>, one per line; a blank line is a place to type and is not written.</summary>
    public ObservableCollection<ChatPlaceRowViewModel> ReadPlaces { get; } = [];

    /// <summary><c>Send:AllowedPlaces</c>, one per line; a blank line is a place to type and is not written.</summary>
    public ObservableCollection<ChatPlaceRowViewModel> SendPlaces { get; } = [];

    /// <summary>Adds a blank line to <see cref="ReadPlaces"/>.</summary>
    public RelayCommand AddReadPlaceCommand { get; }

    /// <summary>Adds a blank line to <see cref="SendPlaces"/>.</summary>
    public RelayCommand AddSendPlaceCommand { get; }

    /// <summary>Removes the line given as parameter from the list that holds it.</summary>
    public RelayCommand RemovePlaceCommand { get; }

    /// <summary>
    /// Whether the places an agent may read show: as soon as the account reads, always for the
    /// expert — but for a webhook, whose address fixes the place —, and whenever the file lists some.
    /// </summary>
    public bool ShowReadPlaces => ReadPlaces.Count > 0 || (!IsWebhook && (Reads || _owner.IsExpert));

    /// <summary>
    /// Whether the places an agent may write in show: as soon as the account writes, always for the
    /// expert — but for a webhook and an application —, and whenever the file lists some.
    /// </summary>
    public bool ShowSendPlaces => SendPlaces.Count > 0 || (IsPerson && (Writes || _owner.IsExpert));

    /// <summary>Whether the account may read and lists no place: the engine then reads nowhere.</summary>
    public bool ShowReadClosed => !IsWebhook && Reads && !HasPlace(ReadPlaces);

    /// <summary>Whether the account may write and lists no place: the engine then writes nowhere.</summary>
    public bool ShowSendClosed => IsPerson && Writes && !HasPlace(SendPlaces);

    /// <summary>Whether the cap of writes shows: never for an application, which writes nothing — unless the file holds one.</summary>
    public bool ShowMaxPerHour => !IsApplication || _maxPerHour.Length > 0;

    // ── signing in ──

    /// <summary>
    /// <c>Auth:Method</c>: who signs in, a person when the file says nothing. Choosing the person
    /// removes the key rather than spelling the engine's default.
    /// </summary>
    public string? AuthMethod
    {
        get => string.IsNullOrEmpty(_authMethod) ? ChatSection.Values.OAuth2 : Shown(ChatSection.AuthMethods, _authMethod);
        set
        {
            if (string.IsNullOrWhiteSpace(value) || string.Equals(AuthMethod, value, StringComparison.Ordinal))
                return;

            _authMethod = value == ChatSection.Values.OAuth2 ? null : value;
            OnPropertyChanged();
            Commit();
        }
    }

    /// <summary>Whether the method shows: the expert, on a Teams account — or a file that names one.</summary>
    public bool ShowAuthMethod => (_owner.IsExpert && !IsWebhook) || !string.IsNullOrEmpty(_authMethod);

    /// <summary>Whether the application and the tenant show: never for a webhook, unless the file holds one.</summary>
    public bool ShowApplication => !IsWebhook || _clientId.Length > 0 || _tenant.Length > 0;

    /// <summary><c>Auth:ClientId</c>: the id of the Microsoft Entra application.</summary>
    public string ClientId
    {
        get => _clientId;
        set => SetText(ref _clientId, value);
    }

    /// <summary><c>Auth:Tenant</c>: the Microsoft tenant.</summary>
    public string Tenant
    {
        get => _tenant;
        set => SetText(ref _tenant, value);
    }

    /// <summary>Whether the administrator's grants show: to an account a person signs in, or one whose file lists some.</summary>
    public bool ShowGrants => IsPerson || _adminGranted.Any(entry => !string.IsNullOrWhiteSpace(entry));

    /// <summary>An administrator granted the reading of channel messages.</summary>
    public bool GrantChannelMessages
    {
        get => IsGranted(ChatSection.Values.ChannelMessages);
        set => SetGrant(ChatSection.Values.ChannelMessages, value);
    }

    /// <summary>An administrator granted editing and deleting in a channel.</summary>
    public bool GrantChannelEdit
    {
        get => IsGranted(ChatSection.Values.ChannelEdit);
        set => SetGrant(ChatSection.Values.ChannelEdit, value);
    }

    /// <summary>An administrator granted the reading of meeting transcripts.</summary>
    public bool GrantMeetings
    {
        get => IsGranted(ChatSection.Values.MeetingsGrant);
        set => SetGrant(ChatSection.Values.MeetingsGrant, value);
    }

    /// <summary>An administrator granted the list of the members of a channel.</summary>
    public bool GrantChannelMembers
    {
        get => IsGranted(ChatSection.Values.ChannelMembers);
        set => SetGrant(ChatSection.Values.ChannelMembers, value);
    }

    /// <summary>
    /// <c>Cloud</c>, the worldwide one when the file says nothing. Choosing that one removes the
    /// key rather than spelling the engine's default.
    /// </summary>
    public string? Cloud
    {
        get => string.IsNullOrEmpty(_cloud) ? ChatSection.Values.Global : Shown(ChatSection.Clouds, _cloud);
        set
        {
            if (string.IsNullOrWhiteSpace(value) || string.Equals(Cloud, value, StringComparison.Ordinal))
                return;

            _cloud = value == ChatSection.Values.Global ? null : value;
            OnPropertyChanged();
            Commit();
        }
    }

    /// <summary>Whether the Microsoft cloud shows: the expert, on a Teams account — or a file that names one.</summary>
    public bool ShowCloud => (_owner.IsExpert && !IsWebhook) || !string.IsNullOrEmpty(_cloud);

    /// <summary><c>Auth:CertificatePathEnvVar</c>: the <i>name</i> of the variable that holds the path of the certificate.</summary>
    public string CertificatePathEnvVar
    {
        get => _certificatePathEnvVar;
        set => SetText(ref _certificatePathEnvVar, value);
    }

    /// <summary><c>Auth:FederatedTokenFileEnvVar</c>: the <i>name</i> of the variable that holds the path of the federated token file.</summary>
    public string FederatedTokenFileEnvVar
    {
        get => _federatedTokenFileEnvVar;
        set => SetText(ref _federatedTokenFileEnvVar, value);
    }

    /// <summary><c>Auth:ClientSecretEnvVar</c>: the <i>name</i> of the variable that holds the client secret.</summary>
    public string ClientSecretEnvVar
    {
        get => _clientSecretEnvVar;
        set => SetText(ref _clientSecretEnvVar, value);
    }

    /// <summary><c>Webhook:UrlEnvVar</c>: the <i>name</i> of the variable that holds the webhook's address.</summary>
    public string WebhookEnvVar
    {
        get => _webhookEnvVar;
        set => SetText(ref _webhookEnvVar, value);
    }

    // ── what the run will say ──

    /// <summary>
    /// What the run will say of the account as the file holds it: a key the engine does not know
    /// and a value it cannot read first, the engine's rules otherwise. Each is a warning — an
    /// incomplete account stops no save.
    /// </summary>
    public IReadOnlyList<ValidationMessageViewModel> Problems => _problems;

    /// <summary>Whether the run will set the account aside.</summary>
    public bool HasProblems => _problems.Count > 0;

    // ── what the engine makes of the account, and the connection test ──

    /// <summary>
    /// What <c>orkeon chat accounts</c> answered for the account as the file was last saved;
    /// <see cref="EmailAccountReadiness.None"/> until an answer names it.
    /// </summary>
    public EmailAccountReadiness StateKind
    {
        get
        {
            if (_stateFailure is not null)
                return EmailAccountReadiness.Unknown;

            return _state switch
            {
                null => EmailAccountReadiness.None,
                { IsSetAside: true } => EmailAccountReadiness.SetAside,
                { Ready: true } => EmailAccountReadiness.Ready,
                _ => EmailAccountReadiness.NotReady,
            };
        }
    }

    /// <summary>
    /// The state, localized: "Ready", "Not ready", "Set aside", or "Unknown — why" when the CLI
    /// is missing or failed; null while there is none.
    /// </summary>
    public string? State => StateKind switch
    {
        EmailAccountReadiness.Ready => _strings[StudioStringKeys.MailStateReady],
        EmailAccountReadiness.NotReady => _strings[StudioStringKeys.MailStateNotReady],
        EmailAccountReadiness.SetAside => _strings[StudioStringKeys.MailStateSetAside],
        EmailAccountReadiness.Unknown => _strings.Format(
            StudioStringKeys.MailStateUnknown, _owner.FailureText(_stateFailure!)),
        _ => null,
    };

    /// <summary>
    /// The engine's sentence on what a not-ready account lacks, in English, as printed — it names
    /// the variable or the sign-in to run. Null otherwise.
    /// </summary>
    public string? StateDetail =>
        StateKind == EmailAccountReadiness.NotReady && _state?.Problem is { Length: > 0 } problem ? problem : null;

    /// <summary>Whether the row has a state to show.</summary>
    public bool HasState => StateKind != EmailAccountReadiness.None;

    /// <summary>Whether <see cref="StateDetail"/> has something to say.</summary>
    public bool HasStateDetail => StateDetail is not null;

    /// <summary>Whether the engine found the account ready to connect.</summary>
    public bool IsReady => StateKind == EmailAccountReadiness.Ready;

    /// <summary>
    /// "Test the connection": runs <c>orkeon chat check</c> on the account as the saved file
    /// declares it — a real connection that asks Microsoft who signs in and what the rights reach. Only ever started
    /// here, by a click; disabled while the document holds unsaved edits.
    /// </summary>
    public AsyncRelayCommand TestCommand { get; }

    /// <summary>Whether a connection test is in flight.</summary>
    public bool IsTesting
    {
        get => _isTesting;
        private set => SetProperty(ref _isTesting, value);
    }

    /// <summary>
    /// The verdict of the last connection test; null before any, while one runs, after one that
    /// was stopped, and as soon as the account is edited or renamed.
    /// </summary>
    public EmailCheckOutcome? LastCheck
    {
        get => _lastCheck;
        private set
        {
            if (SetProperty(ref _lastCheck, value))
            {
                OnPropertiesChanged(
                    nameof(HasLastCheck), nameof(LastCheckReachable), nameof(LastCheckHeadline), nameof(LastCheckDetail),
                    nameof(LastCheckHint), nameof(HasLastCheckHint));
            }
        }
    }

    /// <summary>Whether a verdict shows.</summary>
    public bool HasLastCheck => _lastCheck is not null;

    /// <summary>Whether the last test connected.</summary>
    public bool LastCheckReachable => _lastCheck?.Kind == EmailCheckKind.Reachable;

    /// <summary>The verdict, localized; a refusal Studio answers with a gesture of its own names that gesture.</summary>
    public string? LastCheckHeadline => _lastCheck switch
    {
        null => null,
        { Code: EmailCheckCodes.SignInRequired } when ShowSignIn => _strings[StudioStringKeys.MailTestSignInFirst],
        // The check of a webhook calls nothing: only a post would tell.
        { Kind: EmailCheckKind.Reachable } when IsWebhook => _strings[StudioStringKeys.ChatTestWebhook],
        { Kind: EmailCheckKind.Reachable } => _strings[StudioStringKeys.MailTestReachable],
        { Kind: EmailCheckKind.OperatorFixable } => _strings[StudioStringKeys.MailTestOperatorFixable],
        { Kind: EmailCheckKind.ServerOrNetwork } => _strings[StudioStringKeys.MailTestServerOrNetwork],
        _ => _strings[StudioStringKeys.MailTestFailed],
    };

    /// <summary>
    /// What to do from here, when the engine's sentence sends to a terminal: « Sign in » on the row.
    /// Null for every other verdict.
    /// </summary>
    public string? LastCheckHint =>
        ShowSignIn && _lastCheck?.Code == EmailCheckCodes.SignInRequired ? _strings[StudioStringKeys.MailTestSignInFirstHint] : null;

    /// <summary>Whether the verdict comes with a gesture of Studio's.</summary>
    public bool HasLastCheckHint => LastCheckHint is not null;

    /// <summary>
    /// The engine's sentence, in English, as printed: what the account reached on success, what was
    /// refused otherwise. Without a CLI, the sentence of the home screen.
    /// </summary>
    public string? LastCheckDetail => _lastCheck is { Kind: EmailCheckKind.Unavailable, ExitCode: null }
        ? _owner.EngineMissingText
        : _lastCheck?.Sentence;

    // ── signing in and out ──

    /// <summary>Whether a person signs the account in: a Teams account, unless an application does.</summary>
    public bool ShowSignIn => ChatAccountRules.ProviderOf(_provider) == ChatSection.Values.Teams && !IsApplication;

    /// <summary>
    /// "Sign in": runs <c>orkeon chat login</c> on the account as the saved file declares it, and
    /// opens the panel that says what to do meanwhile. Disabled while the document holds unsaved
    /// edits — the verb reads the file — and while a sign-in of this account waits.
    /// </summary>
    public RelayCommand SignInCommand { get; }

    /// <summary>The sign-in panel of the account: in flight, or ended and not closed yet; null otherwise.</summary>
    public EmailSignInViewModel? SignIn
    {
        get => _signIn;
        private set
        {
            if (!SetProperty(ref _signIn, value))
                return;

            OnPropertyChanged(nameof(HasSignIn));
            RefreshTest();
        }
    }

    /// <summary>Whether a sign-in panel shows.</summary>
    public bool HasSignIn => _signIn is not null;

    /// <summary>"Sign out": asks first — nothing is forgotten until <see cref="ConfirmSignOutCommand"/>.</summary>
    public RelayCommand SignOutCommand { get; }

    /// <summary>Whether the sign-out question shows.</summary>
    public bool IsConfirmingSignOut
    {
        get => _isConfirmingSignOut;
        private set
        {
            if (SetProperty(ref _isConfirmingSignOut, value))
                OnPropertyChanged(nameof(IsIdle));
        }
    }

    /// <summary>"Forget the stored tokens of {name}? The account will need a new sign-in.", localized.</summary>
    public string SignOutConfirmText =>
        _strings.Format(StudioStringKeys.MailSignOutConfirm, _name);

    /// <summary>Runs <c>orkeon chat logout</c> on the account, then reads the states again.</summary>
    public AsyncRelayCommand ConfirmSignOutCommand { get; }

    /// <summary>Closes the sign-out question; nothing was forgotten.</summary>
    public RelayCommand CancelSignOutCommand { get; }

    /// <summary>Why the last sign-out did not happen, as the verb said it; null after one that did, and before any.</summary>
    public string? SignOutFailure
    {
        get => _signOutFailure;
        private set
        {
            if (SetProperty(ref _signOutFailure, value))
                OnPropertyChanged(nameof(HasSignOutFailure));
        }
    }

    /// <summary>Whether a sign-out failure shows.</summary>
    public bool HasSignOutFailure => _signOutFailure is not null;

    // ── what each mode sees ──

    /// <summary>Whether the expert's fields show.</summary>
    public bool ShowExpertFields => _owner.IsExpert;

    // The fields of a credential show when the kind of the account calls for them, or when the file
    // already holds them: a key the file carries must stay reachable, and a key nothing uses is
    // noise, which the user reads as something to fill.

    /// <summary>Whether the three variables of an application's credential show: the expert, on such an account.</summary>
    public bool ShowCertificateEnvVar => ShowsVariable(IsApplication, _certificatePathEnvVar);

    /// <summary>Whether the variable of the federated token file shows.</summary>
    public bool ShowFederatedTokenFileEnvVar => ShowsVariable(IsApplication, _federatedTokenFileEnvVar);

    /// <summary>Whether the variable of the client secret shows.</summary>
    public bool ShowClientSecretEnvVar => ShowsVariable(IsApplication, _clientSecretEnvVar);

    /// <summary>Whether the variable of the webhook's address shows.</summary>
    public bool ShowWebhookEnvVar => ShowsVariable(IsWebhook, _webhookEnvVar);

    // ── the secret: typed here, kept outside any file ──

    /// <summary>
    /// The line the secret is typed on: the address of a webhook, the client secret of an
    /// application that names neither a certificate nor a federated token; null otherwise. It stores
    /// under the variable the file names, else under the one derived from the account's name — and
    /// the first value kept writes that name.
    /// </summary>
    public SecretRowViewModel? Secret => _secret;

    /// <summary>Whether the secret line shows.</summary>
    public bool ShowSecret => _secret is not null;

    /// <summary>What the secret is, localized: the webhook's address, or the application's client secret.</summary>
    public string SecretLabel =>
        _strings[IsWebhook ? StudioStringKeys.ChatWebhookUrl : StudioStringKeys.ChatApplicationSecret];

    /// <summary>Whether the run will find the secret: the file names a variable, and it holds a value.</summary>
    public bool SecretStored => _secretKept && !string.IsNullOrWhiteSpace(SecretVariable);

    /// <summary>"Stored on this machine, outside any file" / "Not stored yet", localized — never the value.</summary>
    public string SecretStatus =>
        _strings[SecretStored ? StudioStringKeys.MailPasswordStored : StudioStringKeys.MailPasswordMissing];

    /// <summary>Where the secret is kept, for the expert: "Kept in the variable X"; null for the novice, and while nothing is kept.</summary>
    public string? SecretVariableLine =>
        _owner.IsExpert && SecretStored && _secret is not null
            ? _strings.Format(StudioStringKeys.MailSecretVariable, _secret.EnvName)
            : null;

    /// <summary>The names the account's fields hold for its variables, as typed.</summary>
    internal IEnumerable<string> SecretVariables =>
        [_clientSecretEnvVar, _certificatePathEnvVar, _federatedTokenFileEnvVar, _webhookEnvVar];

    private string SecretVariable => IsWebhook ? _webhookEnvVar : _clientSecretEnvVar;

    // ── renaming and removing, in place of the action row ──

    /// <summary>Whether the rename editor shows.</summary>
    public bool IsRenaming
    {
        get => _isRenaming;
        private set
        {
            if (SetProperty(ref _isRenaming, value))
                RefreshQuestion();
        }
    }

    /// <summary>The new name, as typed.</summary>
    public string RenameText
    {
        get => _renameText;
        set
        {
            if (SetProperty(ref _renameText, value ?? ""))
                RefreshQuestion();
        }
    }

    /// <summary>Why the typed name cannot be used, localized; null when it can, and while the editor is closed.</summary>
    public string? RenameRefusal => _isRenaming ? _owner.NameRefusal(_renameText.Trim(), this) : null;

    /// <summary>Whether the rename editor has a refusal to show.</summary>
    public bool HasRenameRefusal => RenameRefusal is not null;

    /// <summary>Moves the account under the typed name.</summary>
    public RelayCommand ConfirmRenameCommand { get; }

    /// <summary>Closes the rename editor; nothing moved.</summary>
    public RelayCommand CancelRenameCommand { get; }

    /// <summary>Whether the removal confirmation shows.</summary>
    public bool IsConfirmingRemove
    {
        get => _isConfirmingRemove;
        private set
        {
            if (SetProperty(ref _isConfirmingRemove, value))
                OnPropertyChanged(nameof(IsIdle));
        }
    }

    /// <summary>Removes the account.</summary>
    public RelayCommand ConfirmRemoveCommand { get; }

    /// <summary>Disarms the removal confirmation.</summary>
    public RelayCommand CancelRemoveCommand { get; }

    /// <summary>Whether the row shows its action row: no question is open.</summary>
    public bool IsIdle => !_isRenaming && !_isConfirmingRemove && !_isConfirmingSignOut;

    internal void BeginRename()
    {
        _renameText = _name;
        OnPropertyChanged(nameof(RenameText));
        IsRenaming = true;
    }

    internal void BeginRemove() => IsConfirmingRemove = true;

    internal void BeginSignOut()
    {
        SignOutFailure = null;
        IsConfirmingSignOut = true;
        ConfirmSignOutCommand.RaiseCanExecuteChanged();
    }

    internal void Disarm()
    {
        IsRenaming = false;
        IsConfirmingRemove = false;
        IsConfirmingSignOut = false;
    }

    /// <summary>The panel is done with — cancelled, closed, or stopped with the tab: it leaves the row.</summary>
    internal void SignInClosed(EmailSignInViewModel signIn)
    {
        if (ReferenceEquals(_signIn, signIn))
            SignIn = null;
    }

    /// <summary>The account moved under <paramref name="name"/> in the document: the row follows.</summary>
    internal void Renamed(string name)
    {
        ForgetTest();
        _name = name;
        _loaded = _loaded with { Name = name };
        OnPropertiesChanged(nameof(Name), nameof(SignOutConfirmText));
    }

    internal void RefreshMode()
    {
        OnPropertiesChanged(
            nameof(ShowExpertFields), nameof(ShowReadPlaces), nameof(ShowSendPlaces), nameof(ShowAuthMethod), nameof(ShowCloud),
            nameof(ShowCertificateEnvVar), nameof(ShowFederatedTokenFileEnvVar), nameof(ShowClientSecretEnvVar),
            nameof(ShowWebhookEnvVar), nameof(SecretVariableLine));
    }

    /// <summary>
    /// Puts the secret line on the variable it stores under: the one the file names, else the one
    /// derived from the account's name, clear of what the accounts of the file already name. A
    /// line whose variable did not move is kept, with what is typed in it — and with what the
    /// store said of its variable: the store is asked when a line is built, and again only when a
    /// value is kept (<see cref="SecretKeptUnder"/>), never at an edit.
    /// </summary>
    internal void RefreshSecrets()
    {
        var taken = _owner.SecretVariables.ToList();
        var variable = SecretVariableToStore(taken);
        // Two kinds may name one variable: the kind alone then moves the line.
        if (!string.Equals(_secret?.EnvName, variable, StringComparison.Ordinal) || _secretIsWebhook != IsWebhook)
        {
            // Never carried over from one kind of secret to the other: the line is another.
            var typed = _secretIsWebhook == IsWebhook ? _secret?.KeyInput ?? "" : "";
            if (_secret is not null)
                _secret.Stored -= OnSecretStored;

            _secret = variable is null
                ? null
                : new SecretRowViewModel(variable, () => SecretLabel, _owner.KeyStore, _strings) { KeyInput = typed };
            _secretIsWebhook = IsWebhook;
            if (_secret is not null)
                _secret.Stored += OnSecretStored;

            _secretKept = _secret is { HasKey: true };
            OnPropertiesChanged(nameof(Secret), nameof(ShowSecret), nameof(SecretLabel));
        }

        RefreshSecretStates();
    }

    /// <summary>
    /// A value was kept under <paramref name="variable"/>, by this account or by another that
    /// names the same one: the line that stores under it asks the store again, and says it.
    /// </summary>
    internal void SecretKeptUnder(string variable)
    {
        if (!string.Equals(_secret?.EnvName, variable, StringComparison.Ordinal))
            return;

        _secretKept = _secret!.HasKey;
        RefreshSecretStates();
    }

    /// <summary>
    /// What the last reading of the saved file says of the account: the engine's answer for its
    /// name, or why no answer came back; neither when the file does not hold the account.
    /// </summary>
    internal void SetState(ChatAccountState? state, EmailCliFailure? failure)
    {
        _state = state;
        _stateFailure = failure;
        RefreshState();
    }

    /// <summary>
    /// The account moved — edited, renamed, removed: the test in flight is stopped, and neither
    /// its verdict nor the one that shows says anything of the account as it now stands.
    /// </summary>
    internal void ForgetTest()
    {
        var test = _test;
        _test = null;
        try
        {
            test?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The test ended between the edit and the cancel; nothing left to stop.
        }

        LastCheck = null;
    }

    /// <summary>The document was edited or saved, or a sign-in moved: the test, the sign-in and the sign-out ask again whether they may run.</summary>
    internal void RefreshTest()
    {
        TestCommand.RaiseCanExecuteChanged();
        SignInCommand.RaiseCanExecuteChanged();
        SignOutCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Reads again what the run will say of the account, as the document now holds it.</summary>
    internal void RefreshProblems()
    {
        _problems = [.. ChatAccountRules.Check(_owner.CurrentDocument, _name).Select(message => new ValidationMessageViewModel(message, _strings))];
        OnPropertiesChanged(nameof(Problems), nameof(HasProblems));
    }

    /// <summary>A language change: the findings, the labels and the refusal are said again.</summary>
    internal void RefreshTexts()
    {
        RefreshProblems();
        _secret?.RefreshTexts();
        RefreshSecretStates();
        RefreshState();
        _signIn?.RefreshTexts();
        OnPropertiesChanged(
            nameof(RenameRefusal), nameof(HasRenameRefusal), nameof(SecretLabel),
            nameof(LastCheckHeadline), nameof(LastCheckDetail), nameof(LastCheckHint), nameof(SignOutConfirmText));
    }

    /// <summary>A place was typed in: the list is written again.</summary>
    internal void PlaceEdited() => Commit();

    /// <summary>
    /// The account as the fields hold it. A field left blank is null — an absent key —, a text
    /// the user did not change is the one the file held, and a number field that holds no number
    /// keeps its last one.
    /// </summary>
    internal ChatAccountDefinition ToDefinition() => new()
    {
        Name = _name,
        Provider = _provider,
        Rights = _rights,
        RightsRaw = _rightsRaw,
        AuthMethod = _authMethod,
        ClientId = Text(_clientId, _loaded.ClientId),
        Tenant = Text(_tenant, _loaded.Tenant),
        AdminGranted = _adminGranted,
        CertificatePathEnvVar = Text(_certificatePathEnvVar, _loaded.CertificatePathEnvVar),
        FederatedTokenFileEnvVar = Text(_federatedTokenFileEnvVar, _loaded.FederatedTokenFileEnvVar),
        ClientSecretEnvVar = Text(_clientSecretEnvVar, _loaded.ClientSecretEnvVar),
        WebhookEnvVar = Text(_webhookEnvVar, _loaded.WebhookEnvVar),
        Cloud = _cloud,
        ReadPlaces = Written(ReadPlaces),
        SendPlaces = Written(SendPlaces),
        MaxPerHour = _maxPerHourNumber,
    };

    /// <summary>Whether an application signs in as itself.</summary>
    private bool IsApplication => !IsWebhook && ChatAccountRules.MethodOf(_authMethod) == ChatSection.Values.ClientCredentials;

    /// <summary>Whether a person signs in: what an account with no readable service is taken for.</summary>
    private bool IsPerson => !IsWebhook && !IsApplication;

    private bool Reads => CanRead || CanMeetings;

    private bool Writes => CanSend || CanEdit || CanDelete || CanReact;

    private bool Has(ChatRights right) => (_rights & right) != ChatRights.None;

    private static bool HasPlace(IEnumerable<ChatPlaceRowViewModel> places) => places.Any(line => !string.IsNullOrWhiteSpace(line.Place));

    private static ImmutableList<string> Written(IEnumerable<ChatPlaceRowViewModel> places) =>
        [.. places.Select(line => line.Place.Trim()).Where(place => place.Length > 0)];

    private bool ShowsVariable(bool called, string name) => (_owner.IsExpert && called) || !string.IsNullOrWhiteSpace(name);

    private bool IsGranted(string grant) => _adminGranted.Any(entry => Names(entry, grant));

    private static bool Names(string entry, string grant) => string.Equals(entry.Trim(), grant, StringComparison.OrdinalIgnoreCase);

    /// <summary>Adds or removes one grant; an entry that is none stays, for the engine to refuse and the user to read.</summary>
    private void SetGrant(string grant, bool granted, [CallerMemberName] string? propertyName = null)
    {
        if (IsGranted(grant) == granted)
            return;

        _adminGranted = granted ? _adminGranted.Add(grant) : _adminGranted.RemoveAll(entry => Names(entry, grant));
        OnPropertyChanged(propertyName);
        Commit();
    }

    /// <summary>
    /// The variable the secret line stores under, or null when the account has no secret to type: an
    /// application whose file names another credential keeps that one, the engine taking exactly one.
    /// </summary>
    private string? SecretVariableToStore(IEnumerable<string> taken)
    {
        if (IsWebhook)
            return NamedOrDerived(_webhookEnvVar, ChatSecretNames.WebhookVariableFor(_name, taken));
        if (!IsApplication)
            return null;

        var other = !string.IsNullOrWhiteSpace(_certificatePathEnvVar) || !string.IsNullOrWhiteSpace(_federatedTokenFileEnvVar);
        return other && string.IsNullOrWhiteSpace(_clientSecretEnvVar)
            ? null
            : NamedOrDerived(_clientSecretEnvVar, ChatSecretNames.ClientSecretFor(_name, taken));
    }

    private void SetRight(ChatRights right, bool granted, [CallerMemberName] string? propertyName = null)
    {
        if (Has(right) == granted)
            return;

        _rights = granted ? _rights | right : _rights & ~right;
        // A switch was touched: what the file said and the engine could not read is replaced.
        _rightsRaw = null;
        OnPropertyChanged(propertyName);
        Commit();
    }

    private void SetText(ref string field, string? value, [CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, value ?? "", propertyName))
            Commit();
    }

    private void SetNumber(ref string text, ref int? number, string? value, [CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref text, value ?? "", propertyName))
            return;

        OnPropertyChanged(nameof(HasUnreadNumber));
        var typed = text.Trim();
        if (typed.Length == 0)
            number = null;
        else if (int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            number = parsed;
        else
            return;

        Commit();
    }

    private void RemovePlace(ChatPlaceRowViewModel line)
    {
        if (ReadPlaces.Remove(line) || SendPlaces.Remove(line))
            Commit();
    }

    private void ConfirmRename()
    {
        if (_isRenaming && _owner.Rename(this, _renameText.Trim()))
            IsRenaming = false;
    }

    private void RefreshQuestion()
    {
        OnPropertiesChanged(nameof(RenameRefusal), nameof(HasRenameRefusal), nameof(IsIdle));
        ConfirmRenameCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Writes the account, then publishes what the edit moved: what each mode sees.</summary>
    private void Commit()
    {
        ForgetTest();
        var definition = ToDefinition();
        _owner.Write(this);
        _loaded = definition;
        OnPropertiesChanged(
            nameof(RightsLine), nameof(IsWebhook), nameof(ShowApplication),
            nameof(ShowRead), nameof(ShowSend), nameof(ShowEdit), nameof(ShowDelete), nameof(ShowReact), nameof(ShowMeetings),
            nameof(ShowReadPlaces), nameof(ShowSendPlaces), nameof(ShowReadClosed), nameof(ShowSendClosed), nameof(ShowMaxPerHour),
            nameof(ShowSignIn), nameof(ShowAuthMethod), nameof(ShowGrants), nameof(ShowCloud),
            nameof(ShowCertificateEnvVar), nameof(ShowFederatedTokenFileEnvVar), nameof(ShowClientSecretEnvVar),
            nameof(ShowWebhookEnvVar));
        RefreshTest();
    }

    /// <summary>
    /// A value was kept. The first one names its variable in the file — the derived name, written
    /// like a name the expert typed; a name the file already holds is never written again.
    /// </summary>
    private void OnSecretStored(object? sender, EventArgs e)
    {
        if (sender is not SecretRowViewModel line)
            return;

        _owner.SecretStoredUnder(line.EnvName);
        if (ReferenceEquals(line, _secret) && string.IsNullOrWhiteSpace(SecretVariable))
        {
            if (IsWebhook)
                WebhookEnvVar = line.EnvName;
            else
                ClientSecretEnvVar = line.EnvName;
        }

        // A value under a variable the file already names saves nothing: the engine is asked again,
        // once — with the save of a name just written, when there is one.
        _owner.SecretKept();
    }

    /// <summary>One connection test: "connecting" while the CLI runs, then its verdict — none when the test was stopped.</summary>
    private async Task TestAsync()
    {
        using var test = new CancellationTokenSource();
        _test = test;
        LastCheck = null;
        IsTesting = true;
        try
        {
            var outcome = await _owner.CheckAsync(_name, test.Token);
            // The account moved while the child answered: its verdict is of the account as it was.
            if (ReferenceEquals(_test, test))
                LastCheck = outcome;
        }
        finally
        {
            if (ReferenceEquals(_test, test))
                _test = null;

            IsTesting = false;
        }
    }

    /// <summary>Whether the account may be signed in or out now: a person signs it in, a CLI to ask, and nothing unsaved.</summary>
    private bool CanSign => ShowSignIn && _owner.CanTest;

    private void BeginSignIn()
    {
        if (!SignInCommand.CanExecute(null))
            return;

        // A panel that ended and was left open makes way for the new one.
        _signIn?.Abandon();
        SignOutFailure = null;
        SignIn = _owner.BeginSignIn(this);
    }

    /// <summary>
    /// The confirmed sign-out: the question closes, a sign-in of the account still waiting is
    /// stopped — its tokens would land after the ones being forgotten —, and the verb runs.
    /// </summary>
    private async Task SignOutAsync()
    {
        IsConfirmingSignOut = false;
        ConfirmSignOutCommand.RaiseCanExecuteChanged();
        _signIn?.Abandon();
        SignOutFailure = await _owner.SignOutAsync(_name) is { } failure ? _owner.FailureText(failure) : null;
    }

    private void RefreshState() =>
        OnPropertiesChanged(
            nameof(StateKind), nameof(State), nameof(StateDetail), nameof(HasState), nameof(HasStateDetail), nameof(IsReady));

    private void RefreshSecretStates() =>
        OnPropertiesChanged(nameof(SecretStored), nameof(SecretStatus), nameof(SecretVariableLine));

    /// <summary>The engine's spelling of <paramref name="written"/> when it is one of <paramref name="names"/> but for the case, else the text as written.</summary>
    private static string? Shown(IReadOnlyList<string> names, string? written) =>
        written is null
            ? null
            : names.FirstOrDefault(name => string.Equals(name, written, StringComparison.OrdinalIgnoreCase)) ?? written;

    /// <summary>The text to write: the file's own when the field still shows it, else what was typed, trimmed, and null when blank.</summary>
    private static string? Text(string typed, string? loaded)
    {
        if (string.Equals(typed, loaded ?? "", StringComparison.Ordinal))
            return loaded;
        return string.IsNullOrWhiteSpace(typed) ? null : typed.Trim();
    }

    /// <summary>The variable name as written when one was, else the derived one.</summary>
    private static string NamedOrDerived(string written, string derived) =>
        written.Trim() is { Length: > 0 } named ? named : derived;

    private static string NumberText(int? number) => number?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static bool Unread(string text) =>
        text.Trim() is { Length: > 0 } typed && !int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
}

/// <summary>
/// One line of <c>Read:AllowedPlaces</c> or <c>Send:AllowedPlaces</c>. A line that is no place says
/// so on itself, and is written all the same — the run then says it too.
/// </summary>
public sealed class ChatPlaceRowViewModel : ObservableObject
{
    private readonly ChatAccountRowViewModel _owner;
    private string _place;

    internal ChatPlaceRowViewModel(ChatAccountRowViewModel owner, string place)
    {
        _owner = owner;
        _place = place;
    }

    /// <summary>The place, as typed.</summary>
    public string Place
    {
        get => _place;
        set
        {
            if (!SetProperty(ref _place, value ?? ""))
                return;

            OnPropertyChanged(nameof(IsInvalid));
            _owner.PlaceEdited();
        }
    }

    /// <summary>Whether the line holds something the engine reads as no place.</summary>
    public bool IsInvalid => _place.Trim() is { Length: > 0 } place && !ChatAccountRules.IsPlace(place);
}
