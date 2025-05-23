using System.Globalization;
using System.Linq;
using System.Text;
using Content.Server.Administration.Logs;
using Content.Server.Administration.Managers;
using Content.Server.Backmen.Chat;
using Content.Server.Backmen.Language;
using Content.Server.Chat.Managers;
using Content.Server.Corvax.TTS;
using Content.Server.GameTicking;
using Content.Server.Players;
using Content.Server.Popups;
using Content.Server.SS220.Chat.Systems;
using Content.Server.Players.RateLimiting;
using Content.Server.Speech.Prototypes;
using Content.Server.Speech.EntitySystems;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared.ActionBlocker;
using Content.Shared.Administration;
using Content.Shared.Backmen.Chat;
using Content.Shared.Backmen.Language;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.Database;
using Content.Shared.Examine;
using Content.Shared.Ghost;
using Content.Shared.IdentityManagement;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Systems;
using Content.Shared.Players;
using Content.Shared.Players.RateLimiting;
using Content.Shared.Radio;
using Content.Shared.Speech;
using Content.Shared.Whitelist;
using Robust.Server.Player;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Console;
using Robust.Shared.Network;
using Robust.Shared.Physics;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Replays;
using Robust.Shared.Utility;

namespace Content.Server.Chat.Systems;

// TODO refactor whatever active warzone this class and chatmanager have become
/// <summary>
///     ChatSystem is responsible for in-simulation chat handling, such as whispering, speaking, emoting, etc.
///     ChatSystem depends on ChatManager to actually send the messages.
/// </summary>
public sealed partial class ChatSystem : SharedChatSystem
{
    [Dependency] private readonly IReplayRecordingManager _replay = default!;
    [Dependency] private readonly IConfigurationManager _configurationManager = default!;
    [Dependency] private readonly IChatManager _chatManager = default!;
    [Dependency] private readonly IChatSanitizationManager _sanitizer = default!;
    [Dependency] private readonly IAdminManager _adminManager = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;
    [Dependency] private readonly ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private readonly StationSystem _stationSystem = default!;
    [Dependency] private readonly MobStateSystem _mobStateSystem = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly NyanoChatSystem _nyanoChatSystem = default!;
    [Dependency] private readonly ReplacementAccentSystem _wordreplacement = default!;
    [Dependency] private readonly EntityWhitelistSystem _whitelistSystem = default!;
    [Dependency] private readonly ExamineSystemShared _examineSystem = default!;
    [Dependency] private readonly LanguageSystem _language = default!;

    // Corvax-TTS-Start: Moved from Server to Shared
    // public const int VoiceRange = 10; // how far voice goes in world units
    // public const int WhisperClearRange = 2; // how far whisper goes while still being understandable, in world units
    // public const int WhisperMuffledRange = 5; // how far whisper goes at all, in world units
    // Corvax-TTS-End
    public const string DefaultAnnouncementSound = "/Audio/Corvax/Announcements/announce.ogg"; // Corvax-Announcements
    public const string CentComAnnouncementSound = "/Audio/Corvax/Announcements/centcomm.ogg"; // Corvax-Announcements
    //start-backmen: languages
    public const float DefaultObfuscationFactor = 0.2f; // Percentage of symbols in a whispered message that can be seen even by "far" listeners
    public readonly Color DefaultSpeakColor = Color.White;
    //end-backmen: languages

    private bool _loocEnabled = true;
    private bool _deadLoocEnabled;
    private bool _critLoocEnabled;
    private readonly bool _adminLoocEnabled = true;

    public override void Initialize()
    {
        base.Initialize();
        CacheEmotes();
        Subs.CVar(_configurationManager, CCVars.LoocEnabled, OnLoocEnabledChanged, true);
        Subs.CVar(_configurationManager, CCVars.DeadLoocEnabled, OnDeadLoocEnabledChanged, true);
        Subs.CVar(_configurationManager, CCVars.CritLoocEnabled, OnCritLoocEnabledChanged, true);

        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnGameChange);
    }

    private void OnLoocEnabledChanged(bool val)
    {
        if (_loocEnabled == val) return;

        _loocEnabled = val;
        _chatManager.DispatchServerAnnouncement(
            Loc.GetString(val ? "chat-manager-looc-chat-enabled-message" : "chat-manager-looc-chat-disabled-message"));
    }

    private void OnDeadLoocEnabledChanged(bool val)
    {
        if (_deadLoocEnabled == val) return;

        _deadLoocEnabled = val;
        _chatManager.DispatchServerAnnouncement(
            Loc.GetString(val ? "chat-manager-dead-looc-chat-enabled-message" : "chat-manager-dead-looc-chat-disabled-message"));
    }

    private void OnCritLoocEnabledChanged(bool val)
    {
        if (_critLoocEnabled == val)
            return;

        _critLoocEnabled = val;
        _chatManager.DispatchServerAnnouncement(
            Loc.GetString(val ? "chat-manager-crit-looc-chat-enabled-message" : "chat-manager-crit-looc-chat-disabled-message"));
    }

    private void OnGameChange(GameRunLevelChangedEvent ev)
    {
        switch (ev.New)
        {
            case GameRunLevel.InRound:
                if (!_configurationManager.GetCVar(CCVars.OocEnableDuringRound))
                    _configurationManager.SetCVar(CCVars.OocEnabled, false);
                break;
            case GameRunLevel.PostRound:
            case GameRunLevel.PreRoundLobby:
                if (!_configurationManager.GetCVar(CCVars.OocEnableDuringRound))
                    _configurationManager.SetCVar(CCVars.OocEnabled, true);
                break;
        }
    }

    /// <summary>
    ///     Sends an in-character chat message to relevant clients.
    /// </summary>
    /// <param name="source">The entity that is speaking</param>
    /// <param name="message">The message being spoken or emoted</param>
    /// <param name="desiredType">The chat type</param>
    /// <param name="hideChat">Whether or not this message should appear in the chat window</param>
    /// <param name="hideLog">Whether or not this message should appear in the adminlog window</param>
    /// <param name="shell"></param>
    /// <param name="player">The player doing the speaking</param>
    /// <param name="nameOverride">The name to use for the speaking entity. Usually this should just be modified via <see cref="TransformSpeakerNameEvent"/>. If this is set, the event will not get raised.</param>
    public void TrySendInGameICMessage(
        EntityUid source,
        string message,
        InGameICChatType desiredType,
        bool hideChat, bool hideLog = false,
        IConsoleShell? shell = null,
        ICommonSession? player = null, string? nameOverride = null,
        bool checkRadioPrefix = true,
        bool ignoreActionBlocker = false,
        LanguagePrototype? languageOverride = null)
    {
        TrySendInGameICMessage(source, message, desiredType, hideChat ? ChatTransmitRange.HideChat : ChatTransmitRange.Normal, hideLog, shell, player, nameOverride, checkRadioPrefix, ignoreActionBlocker, languageOverride);
    }

    /// <summary>
    ///     Sends an in-character chat message to relevant clients.
    /// </summary>
    /// <param name="source">The entity that is speaking</param>
    /// <param name="message">The message being spoken or emoted</param>
    /// <param name="desiredType">The chat type</param>
    /// <param name="range">Conceptual range of transmission, if it shows in the chat window, if it shows to far-away ghosts or ghosts at all...</param>
    /// <param name="shell"></param>
    /// <param name="player">The player doing the speaking</param>
    /// <param name="nameOverride">The name to use for the speaking entity. Usually this should just be modified via <see cref="TransformSpeakerNameEvent"/>. If this is set, the event will not get raised.</param>
    /// <param name="ignoreActionBlocker">If set to true, action blocker will not be considered for whether an entity can send this message.</param>
    public void TrySendInGameICMessage(
        EntityUid source,
        string message,
        InGameICChatType desiredType,
        ChatTransmitRange range,
        bool hideLog = false,
        IConsoleShell? shell = null,
        ICommonSession? player = null,
        string? nameOverride = null,
        bool checkRadioPrefix = true,
        bool ignoreActionBlocker = false,
        LanguagePrototype? languageOverride = null
        )
    {
        if (HasComp<GhostComponent>(source))
        {
            // Ghosts can only send dead chat messages, so we'll forward it to InGame OOC.
            TrySendInGameOOCMessage(source, message, InGameOOCChatType.Dead, range == ChatTransmitRange.HideChat, shell, player);
            return;
        }

        if (player != null && _chatManager.HandleRateLimit(player) != RateLimitStatus.Allowed)
            return;

        // Sus
        if (player?.AttachedEntity is { Valid: true } entity && source != entity)
        {
            return;
        }

        if (!CanSendInGame(message, shell, player))
            return;

        ignoreActionBlocker = CheckIgnoreSpeechBlocker(source, ignoreActionBlocker);

        // this method is a disaster
        // every second i have to spend working with this code is fucking agony
        // scientists have to wonder how any of this was merged
        // coding any game admin feature that involves chat code is pure torture
        // changing even 10 lines of code feels like waterboarding myself
        // and i dont feel like vibe checking 50 code paths
        // so we set this here
        // todo free me from chat code
        if (player != null)
        {
            _chatManager.EnsurePlayer(player.UserId).AddEntity(GetNetEntity(source));
        }

        if (desiredType == InGameICChatType.Speak && message.StartsWith(LocalPrefix))
        {
            // prevent radios and remove prefix.
            checkRadioPrefix = false;
            message = message[1..];
        }

        var language = languageOverride ?? _language.GetLanguage(source);

        var shouldCapitalize = (desiredType != InGameICChatType.Emote);
        var shouldPunctuate = _configurationManager.GetCVar(CCVars.ChatPunctuation);
        // Capitalizing the word I only happens in English, so we check language here
        var shouldCapitalizeTheWordI = (!CultureInfo.CurrentCulture.IsNeutralCulture && CultureInfo.CurrentCulture.Parent.Name == "en")
                                       || (CultureInfo.CurrentCulture.IsNeutralCulture && CultureInfo.CurrentCulture.Name == "en");

        message = SanitizeInGameICMessage(source, message, out var emoteStr, shouldCapitalize, shouldPunctuate, shouldCapitalizeTheWordI);

        // Was there an emote in the message? If so, send it.
        if (player != null && emoteStr != message && emoteStr != null)
        {
            SendEntityEmote(source, emoteStr, range, nameOverride, ignoreActionBlocker);
        }

        // This can happen if the entire string is sanitized out.
        if (string.IsNullOrEmpty(message))
            return;

        // This is really terrible. I hate myself for doing this.
        if (language.SpeechOverride.ChatTypeOverride is { } chatTypeOverride)
            desiredType = chatTypeOverride;

        // This message may have a radio prefix, and should then be whispered to the resolved radio channel
        if (checkRadioPrefix)
        {
            if (TryProccessRadioMessage(source, message, out var modMessage, out var channel))
            {
                SendEntityWhisper(source, modMessage, range, channel, nameOverride, language, hideLog, ignoreActionBlocker);
                return;
            }
        }

        // Otherwise, send whatever type.
        switch (desiredType)
        {
            case InGameICChatType.Speak:
                SendEntitySpeak(source, message, range, nameOverride, language, hideLog, ignoreActionBlocker);
                break;
            case InGameICChatType.Whisper:
                SendEntityWhisper(source, message, range, null, nameOverride, language, hideLog, ignoreActionBlocker);
                break;
            case InGameICChatType.Emote:
                SendEntityEmote(source, message, range, nameOverride, hideLog: hideLog, ignoreActionBlocker: ignoreActionBlocker);
                break;
            case InGameICChatType.Telepathic:
                _nyanoChatSystem.SendTelepathicChat(source, message, range == ChatTransmitRange.HideChat);
                break;
        }
    }

    public void TrySendInGameOOCMessage(
        EntityUid source,
        string message,
        InGameOOCChatType type,
        bool hideChat,
        IConsoleShell? shell = null,
        ICommonSession? player = null
        )
    {
        if (!CanSendInGame(message, shell, player))
            return;

        if (player != null && _chatManager.HandleRateLimit(player) != RateLimitStatus.Allowed)
            return;

        // It doesn't make any sense for a non-player to send in-game OOC messages, whereas non-players may be sending
        // in-game IC messages.
        if (player?.AttachedEntity is not { Valid: true } entity || source != entity)
            return;

        message = SanitizeInGameOOCMessage(message);

        var sendType = type;
        // If dead player LOOC is disabled, unless you are an admin with Moderator perms, send dead messages to dead chat
        if ((_adminManager.IsAdmin(player) && _adminManager.HasAdminFlag(player, AdminFlags.Moderator)) // Override if admin
            || _deadLoocEnabled
            || (!HasComp<GhostComponent>(source) && !_mobStateSystem.IsDead(source))) // Check that player is not dead
        {
        }
        else
            sendType = InGameOOCChatType.Dead;

        // If crit player LOOC is disabled, don't send the message at all.
        if (!_critLoocEnabled && _mobStateSystem.IsCritical(source))
            return;

        switch (sendType)
        {
            case InGameOOCChatType.Dead:
                SendDeadChat(source, player, message, hideChat);
                break;
            case InGameOOCChatType.Looc:
                SendLOOC(source, player, message, hideChat);
                break;
        }
    }

    #region Announcements

    /// <summary>
    /// Dispatches an announcement to all.
    /// </summary>
    /// <param name="message">The contents of the message</param>
    /// <param name="sender">The sender (Communications Console in Communications Console Announcement)</param>
    /// <param name="playSound">Play the announcement sound</param>
    /// <param name="colorOverride">Optional color for the announcement message</param>
    public void DispatchGlobalAnnouncement(
        string message,
        string? sender = null,
        bool playSound = true,
        SoundSpecifier? announcementSound = null,
        Color? colorOverride = null
        )
    {
        sender ??= Loc.GetString("chat-manager-sender-announcement");

        var wrappedMessage = Loc.GetString("chat-manager-sender-announcement-wrap-message", ("sender", sender), ("message", FormattedMessage.EscapeText(message)));
        _chatManager.ChatMessageToAll(ChatChannel.Radio, message, wrappedMessage, default, false, true, colorOverride);
        if (playSound)
        {
            if (sender == Loc.GetString("admin-announce-announcer-default"))
                announcementSound = new SoundPathSpecifier(CentComAnnouncementSound); // Corvax-Announcements: Support custom alert sound from admin panel
            announcementSound ??= new SoundPathSpecifier(DefaultAnnouncementSound);

            var announcementFilename = _audio.GetSound(announcementSound);
            var announcementEv = new AnnouncementSpokeEvent(Filter.Broadcast(), announcementFilename, announcementSound?.Params 
            ?? AudioParams.Default.WithVolume(-2f), message);
            RaiseLocalEvent(announcementEv);
        }
        _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Global station announcement from {sender}: {message}");
    }

    /// <summary>
    /// Dispatches an announcement to players selected by filter.
    /// </summary>
    /// <param name="filter">Filter to select players who will recieve the announcement</param>
    /// <param name="message">The contents of the message</param>
    /// <param name="source">The entity making the announcement (used to determine the station)</param>
    /// <param name="sender">The sender (Communications Console in Communications Console Announcement)</param>
    /// <param name="playDefaultSound">Play the announcement sound</param>
    /// <param name="announcementSound">Sound to play</param>
    /// <param name="colorOverride">Optional color for the announcement message</param>
    public void DispatchFilteredAnnouncement(
        Filter filter,
        string message,
        EntityUid? source = null,
        string? sender = null,
        bool playSound = true,
        SoundSpecifier? announcementSound = null,
        Color? colorOverride = null)
    {
        sender ??= Loc.GetString("chat-manager-sender-announcement");

        var wrappedMessage = Loc.GetString("chat-manager-sender-announcement-wrap-message", ("sender", sender), ("message", FormattedMessage.EscapeText(message)));
        _chatManager.ChatMessageToManyFiltered(filter, ChatChannel.Radio, message, wrappedMessage, source ?? default, false, true, colorOverride);
        if (playSound)
        {
            _audio.PlayGlobal(announcementSound?.ToString() ?? DefaultAnnouncementSound, filter, true, AudioParams.Default.WithVolume(-2f));
        }
        _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Station Announcement from {sender}: {message}");
    }

    /// <summary>
    /// Dispatches an announcement on a specific station
    /// </summary>
    /// <param name="source">The entity making the announcement (used to determine the station)</param>
    /// <param name="message">The contents of the message</param>
    /// <param name="sender">The sender (Communications Console in Communications Console Announcement)</param>
    /// <param name="playDefaultSound">Play the announcement sound</param>
    /// <param name="colorOverride">Optional color for the announcement message</param>
    public void DispatchStationAnnouncement(
        EntityUid source,
        string message,
        string? sender = null,
        bool playDefaultSound = true,
        SoundSpecifier? announcementSound = null,
        Color? colorOverride = null)
    {
        sender ??= Loc.GetString("chat-manager-sender-announcement");

        var wrappedMessage = Loc.GetString("chat-manager-sender-announcement-wrap-message", ("sender", sender), ("message", FormattedMessage.EscapeText(message)));
        var station = _stationSystem.GetOwningStation(source);

        if (station == null)
        {
            // you can't make a station announcement without a station
            return;
        }

        if (!EntityManager.TryGetComponent<StationDataComponent>(station, out var stationDataComp)) return;

        var filter = _stationSystem.GetInStation(stationDataComp);

        _chatManager.ChatMessageToManyFiltered(filter, ChatChannel.Radio, message, wrappedMessage, source, false, true, colorOverride);

        if (announcementSound != null || playDefaultSound) // Corvax-TTS
        {
            announcementSound ??= new SoundPathSpecifier(DefaultAnnouncementSound);
            var announcementEv = new AnnouncementSpokeEvent(filter, _audio.GetSound(announcementSound), AudioParams.Default.WithVolume(-2f), message);
            RaiseLocalEvent(announcementEv);
        }

        _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Station Announcement on {station} from {sender}: {message}");
    }

    #endregion

    #region Private API

    private void SendEntitySpeak(
        EntityUid source,
        string originalMessage,
        ChatTransmitRange range,
        string? nameOverride,
        LanguagePrototype language,
        bool hideLog = false,
        bool ignoreActionBlocker = false
        )
    {
        if (!_actionBlocker.CanSpeak(source) && !ignoreActionBlocker)
            return;

        // The original message
        var message = TransformSpeech(source, originalMessage, language);

        if (message.Length == 0)
            return;

        var speech = GetSpeechVerb(source, message);

        // get the entity's apparent name (if no override provided).
        string name;
        if (nameOverride != null)
        {
            name = nameOverride;
        }
        else
        {
            var nameEv = new TransformSpeakerNameEvent(source, Name(source));
            RaiseLocalEvent(source, nameEv);
            name = nameEv.VoiceName;
            // Check for a speech verb override
            if (nameEv.SpeechVerb != null && _prototypeManager.TryIndex<SpeechVerbPrototype>(nameEv.SpeechVerb, out var proto))
                speech = proto;
        }

        name = FormattedMessage.EscapeText(name);
        // The chat message wrapped in a "x says y" string
        var wrappedMessage = WrapPublicMessage(source, name, message, language: language);
        // The chat message obfuscated via language obfuscation
        var obfuscated = SanitizeInGameICMessage(source, _language.ObfuscateSpeech(message, language), out var emoteStr, true, _configurationManager.GetCVar(CCVars.ChatPunctuation), (!CultureInfo.CurrentCulture.IsNeutralCulture && CultureInfo.CurrentCulture.Parent.Name == "en") || (CultureInfo.CurrentCulture.IsNeutralCulture && CultureInfo.CurrentCulture.Name == "en"));
        // The language-obfuscated message wrapped in a "x says y" string
        var wrappedObfuscated = WrapPublicMessage(source, name, obfuscated, language: language);

        SendInVoiceRange(ChatChannel.Local, name, message, wrappedMessage, obfuscated, wrappedObfuscated, source, range, languageOverride: language);

        var ev = new EntitySpokeEvent(source, message, originalMessage, null, null, language);
        RaiseLocalEvent(source, ev, true);

        // To avoid logging any messages sent by entities that are not players, like vendors, cloning, etc.
        // Also doesn't log if hideLog is true.
        if (!HasComp<ActorComponent>(source) || hideLog == true)
            return;

        if (originalMessage == message)
        {
            if (name != Name(source))
                _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Say from {ToPrettyString(source):user} as {name}: {originalMessage}.");
            else
                _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Say from {ToPrettyString(source):user}: {originalMessage}.");
        }
        else
        {
            if (name != Name(source))
                _adminLogger.Add(LogType.Chat, LogImpact.Low,
                    $"Say from {ToPrettyString(source):user} as {name}, original: {originalMessage}, transformed: {message}.");
            else
                _adminLogger.Add(LogType.Chat, LogImpact.Low,
                    $"Say from {ToPrettyString(source):user}, original: {originalMessage}, transformed: {message}.");
        }
    }

private void SendEntityWhisper(
        EntityUid source,
        string originalMessage,
        ChatTransmitRange range,
        RadioChannelPrototype? channel,
        string? nameOverride,
        LanguagePrototype language,
        bool hideLog = false,
        bool ignoreActionBlocker = false
        )
    {
        if (!_actionBlocker.CanSpeak(source) && !ignoreActionBlocker)
            return;

        var message = TransformSpeech(source, FormattedMessage.RemoveMarkupOrThrow(originalMessage), language);
        if (message.Length == 0)
            return;

        var obfuscatedMessage = ObfuscateMessageReadability(message, 0.2f);

        // get the entity's name by visual identity (if no override provided).
        string nameIdentity = FormattedMessage.EscapeText(nameOverride ?? Identity.Name(source, EntityManager));
        // get the entity's name by voice (if no override provided).
        string name;
        if (nameOverride != null)
        {
            name = nameOverride;
        }
        else
        {
            var nameEv = new TransformSpeakerNameEvent(source, Name(source));
            RaiseLocalEvent(source, nameEv);
            name = nameEv.VoiceName;
        }
        name = FormattedMessage.EscapeText(name);

        // start-backmen: language

        var languageObfuscatedMessage = SanitizeInGameICMessage(source, _language.ObfuscateSpeech(message, language), out var emoteStr, true, _configurationManager.GetCVar(CCVars.ChatPunctuation), (!CultureInfo.CurrentCulture.IsNeutralCulture && CultureInfo.CurrentCulture.Parent.Name == "en") || (CultureInfo.CurrentCulture.IsNeutralCulture && CultureInfo.CurrentCulture.Name == "en"));

        var languageObfuscatedMessageLongRange = ObfuscateMessageReadability(languageObfuscatedMessage, 0.2f);

        var wrappedMessage = WrapWhisperMessage(source,
            "chat-manager-entity-whisper-wrap-message",
            nameIdentity,
            message,
            language);

        var wrappedUnknownMessage = WrapWhisperMessage(source,
            "chat-manager-entity-whisper-unknown-wrap-message",
            nameIdentity,
            message,
            language);

        // end-backmen: language

        var orgMsg = new HashSet<ICommonSession>();
        var obsMsg = new HashSet<ICommonSession>();
        foreach (var (session, data) in GetRecipients(source, WhisperMuffledRange))
        {
            EntityUid listener;

            if (session.AttachedEntity is not { Valid: true } playerEntity)
                continue;
            listener = session.AttachedEntity.Value;

            if (MessageRangeCheck(session, data, range) != MessageRangeCheckResult.Full)
                continue; // Won't get logged to chat, and ghosts are too far away to see the pop-up, so we just won't send it to them.

            var canUnderstandLanguage = _language.CanUnderstand(listener, language.ID); // backmen: language
            // How the entity perceives the message depends on whether it can understand its language


            if (data.Range <= WhisperClearRange)
            {
                var perceivedMessage = FormattedMessage.EscapeText(canUnderstandLanguage ? message : languageObfuscatedMessage); // backmen: language
                var wrappedPerceivedMessage = WrapWhisperMessage(source, "chat-manager-entity-whisper-wrap-message", nameIdentity, perceivedMessage, language);
                _chatManager.ChatMessageToOne(ChatChannel.Whisper, message, wrappedPerceivedMessage, source, false, session.Channel);
            }
            //If listener is too far, they only hear fragments of the message
            else if (_examineSystem.InRangeUnOccluded(source, listener, WhisperMuffledRange))
            {
                var perceivedMessage = FormattedMessage.EscapeText(canUnderstandLanguage ? obfuscatedMessage : languageObfuscatedMessageLongRange); // backmen: language
                var wrappedPerceivedMessage = WrapWhisperMessage(source, "chat-manager-entity-whisper-wrap-message", nameIdentity, perceivedMessage, language);
                _chatManager.ChatMessageToOne(ChatChannel.Whisper, obfuscatedMessage, wrappedPerceivedMessage, source, false, session.Channel);

            }
            //If listener is too far and has no line of sight, they can't identify the whisperer's identity
            else
            {
                _chatManager.ChatMessageToOne(ChatChannel.Whisper, obfuscatedMessage, wrappedUnknownMessage, source, false, session.Channel);
                continue; // backmen: language
            }

            // start-backmen: language
            if (canUnderstandLanguage)
            {
                orgMsg.Add(session);
            }
            else
            {
                obsMsg.Add(session);
            }
            // end-backmen: language
        }

        _replay.RecordServerMessage(new ChatMessage(ChatChannel.Whisper, message, wrappedMessage, GetNetEntity(source), null, MessageRangeHideChatForReplay(range)));

        RaiseLocalEvent(source,
            new EntitySpokeLanguageEvent(
                Filter.Empty().AddPlayers(orgMsg),
                Filter.Empty().AddPlayers(obsMsg),
                source,
                message,
                originalMessage,
                channel,
                true,
                obfuscatedMessage,
                languageObfuscatedMessage,
                languageObfuscatedMessageLongRange),
            true);
        var ev = new EntitySpokeEvent(source, message, originalMessage, channel, obfuscatedMessage, language);
        RaiseLocalEvent(source, ev, true);
        if (!hideLog)
            if (originalMessage == message)
            {
                if (name != Name(source))
                    _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Whisper from {ToPrettyString(source):user} as {name}: {originalMessage}.");
                else
                    _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Whisper from {ToPrettyString(source):user}: {originalMessage}.");
            }
            else
            {
                if (name != Name(source))
                    _adminLogger.Add(LogType.Chat, LogImpact.Low,
                    $"Whisper from {ToPrettyString(source):user} as {name}, original: {originalMessage}, transformed: {message}.");
                else
                    _adminLogger.Add(LogType.Chat, LogImpact.Low,
                    $"Whisper from {ToPrettyString(source):user}, original: {originalMessage}, transformed: {message}.");
            }
    }


    private void SendEntityEmote(
        EntityUid source,
        string action,
        ChatTransmitRange range,
        string? nameOverride,
        bool hideLog = false,
        bool checkEmote = true,
        bool ignoreActionBlocker = false,
        NetUserId? author = null
        )
    {
        if (!_actionBlocker.CanEmote(source) && !ignoreActionBlocker)
            return;

        // get the entity's apparent name (if no override provided).
        var ent = Identity.Entity(source, EntityManager);
        var name = FormattedMessage.EscapeText(nameOverride ?? Name(ent));

        // Emotes use Identity.Name, since it doesn't actually involve your voice at all.
        var wrappedMessage = Loc.GetString("chat-manager-entity-me-wrap-message",
            ("entityName", name),
            ("entity", ent),
            ("message", FormattedMessage.RemoveMarkup(action)));

        if (checkEmote)
            TryEmoteChatInput(source, action);
        SendInVoiceRange(ChatChannel.Emotes, name, action, wrappedMessage, obfuscated: "", obfuscatedWrappedMessage: "", source, range, author);
        if (!hideLog)
            if (name != Name(source))
                _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Emote from {ToPrettyString(source):user} as {name}: {action}");
            else
                _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Emote from {ToPrettyString(source):user}: {action}");
    }


    // ReSharper disable once InconsistentNaming
    private void SendLOOC(EntityUid source, ICommonSession player, string message, bool hideChat)
    {
        var name = FormattedMessage.EscapeText(Identity.Name(source, EntityManager));

        if (_adminManager.IsAdmin(player))
        {
            if (!_adminLoocEnabled) return;
        }
        else if (!_loocEnabled) return;

        // If crit player LOOC is disabled, don't send the message at all.
        if (!_critLoocEnabled && _mobStateSystem.IsCritical(source))
            return;

        var wrappedMessage = Loc.GetString("chat-manager-entity-looc-wrap-message",
            ("entityName", name),
            ("message", FormattedMessage.EscapeText(message)));

        SendInVoiceRange(ChatChannel.LOOC, name, message, wrappedMessage,
            obfuscated: string.Empty,
            obfuscatedWrappedMessage: string.Empty, // will be skipped anyway
            source,
            hideChat ? ChatTransmitRange.HideChat : ChatTransmitRange.Normal,
            player.UserId,
            languageOverride: LanguageSystem.Universal);
        _adminLogger.Add(LogType.Chat, LogImpact.Low, $"LOOC from {player:Player}: {message}");
    }

    private void SendDeadChat(EntityUid source, ICommonSession player, string message, bool hideChat)
    {
        var clients = GetDeadChatClients();
        var playerName = Name(source);
        string wrappedMessage;
        if (_adminManager.IsAdmin(player))
        {
            wrappedMessage = Loc.GetString("chat-manager-send-admin-dead-chat-wrap-message",
                ("adminChannelName", Loc.GetString("chat-manager-admin-channel-name")),
                ("userName", player.Channel.UserName),
                ("message", FormattedMessage.EscapeText(message)));
            _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Admin dead chat from {player:Player}: {message}");
        }
        else
        {
            wrappedMessage = Loc.GetString("chat-manager-send-dead-chat-wrap-message",
                ("deadChannelName", Loc.GetString("chat-manager-dead-channel-name")),
                ("playerName", (playerName)),
                ("message", FormattedMessage.EscapeText(message)));
            _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Dead chat from {player:Player}: {message}");
        }

        _chatManager.ChatMessageToMany(ChatChannel.Dead, message, wrappedMessage, source, hideChat, true, clients.ToList(), author: player.UserId);
    }
    #endregion

    #region Utility

    private enum MessageRangeCheckResult
    {
        Disallowed,
        HideChat,
        Full
    }

    /// <summary>
    ///     If hideChat should be set as far as replays are concerned.
    /// </summary>
    private bool MessageRangeHideChatForReplay(ChatTransmitRange range)
    {
        return range == ChatTransmitRange.HideChat;
    }

    /// <summary>
    ///     Checks if a target as returned from GetRecipients should receive the message.
    ///     Keep in mind data.Range is -1 for out of range observers.
    /// </summary>
    private MessageRangeCheckResult MessageRangeCheck(ICommonSession session, ICChatRecipientData data, ChatTransmitRange range)
    {
        var initialResult = MessageRangeCheckResult.Full;
        switch (range)
        {
            case ChatTransmitRange.Normal:
                initialResult = MessageRangeCheckResult.Full;
                break;
            case ChatTransmitRange.GhostRangeLimit:
                initialResult = (data.Observer && data.Range < 0 && !_adminManager.IsAdmin(session)) ? MessageRangeCheckResult.HideChat : MessageRangeCheckResult.Full;
                break;
            case ChatTransmitRange.HideChat:
                initialResult = MessageRangeCheckResult.HideChat;
                break;
            case ChatTransmitRange.NoGhosts:
                initialResult = (data.Observer && !_adminManager.IsAdmin(session)) ? MessageRangeCheckResult.Disallowed : MessageRangeCheckResult.Full;
                break;
        }
        var insistHideChat = data.HideChatOverride ?? false;
        var insistNoHideChat = !(data.HideChatOverride ?? true);
        if (insistHideChat && initialResult == MessageRangeCheckResult.Full)
            return MessageRangeCheckResult.HideChat;
        if (insistNoHideChat && initialResult == MessageRangeCheckResult.HideChat)
            return MessageRangeCheckResult.Full;
        return initialResult;
    }

    /// <summary>
    ///     Sends a chat message to the given players in range of the source entity.
    /// </summary>
    private void SendInVoiceRange(
        ChatChannel channel,
        string name,
        string message,
        string wrappedMessage,
        string obfuscated,
        string obfuscatedWrappedMessage,
        EntityUid source,
        ChatTransmitRange range,
        NetUserId? author = null,
        LanguagePrototype? languageOverride = null)
    {
        var language = languageOverride ?? _language.GetLanguage(source);

        var orgMsg = new HashSet<ICommonSession>();
        var obsMsg = new HashSet<ICommonSession>();
        foreach (var (session, data) in GetRecipients(source, Transform(source).GridUid == null ? 0.3f : VoiceRange))
        {
            if (session.AttachedEntity != null
                && Transform(session.AttachedEntity.Value).GridUid != Transform(source).GridUid
                && !CheckAttachedGrids(source, session.AttachedEntity.Value))
                continue;

            var entRange = MessageRangeCheck(session, data, range);
            if (entRange == MessageRangeCheckResult.Disallowed)
                continue;
            var entHideChat = entRange == MessageRangeCheckResult.HideChat;
            if (session.AttachedEntity is not { Valid: true } playerEntity)
                continue;
            var listener = session.AttachedEntity.Value;

            var canUnderstand = _language.CanUnderstand(listener, language.ID);
            // If the channel does not support languages, or the entity can understand the message, send the original message, otherwise send the obfuscated version
            if (channel == ChatChannel.LOOC || channel == ChatChannel.Emotes || canUnderstand)
            {
                orgMsg.Add(session);
                _chatManager.ChatMessageToOne(channel, message, wrappedMessage, source, entHideChat, session.Channel, author: author);
            }
            else
            {
                obsMsg.Add(session);
                _chatManager.ChatMessageToOne(channel, obfuscated, obfuscatedWrappedMessage, source, entHideChat, session.Channel, author: author);
            }
        }

        _replay.RecordServerMessage(new ChatMessage(channel, message, wrappedMessage, GetNetEntity(source), null, MessageRangeHideChatForReplay(range)));

        if ((orgMsg.Count > 0 || obsMsg.Count > 0) && (channel & ChatChannel.Local) != 0)
        {
            RaiseLocalEvent(source,
                new EntitySpokeLanguageEvent(
                    Filter.Empty().AddPlayers(orgMsg),
                    Filter.Empty().AddPlayers(obsMsg),
                    source,
                    message,
                    wrappedMessage,
                    null,
                    false,
                    obfuscated)
                );
        }
    }

    /// <summary>
    ///     Returns true if the given player is 'allowed' to send the given message, false otherwise.
    /// </summary>
    private bool CanSendInGame(string message, IConsoleShell? shell = null, ICommonSession? player = null)
    {
        // Non-players don't have to worry about these restrictions.
        if (player == null)
            return true;

        var mindContainerComponent = player.ContentData()?.Mind;

        if (mindContainerComponent == null)
        {
            shell?.WriteError("You don't have a mind!");
            return false;
        }

        if (player.AttachedEntity is not { Valid: true } _)
        {
            shell?.WriteError("You don't have an entity!");
            return false;
        }

        return !_chatManager.MessageCharacterLimit(player, message);
    }

    // ReSharper disable once InconsistentNaming
    private string SanitizeInGameICMessage(EntityUid source, string message, out string? emoteStr, bool capitalize = true, bool punctuate = false, bool capitalizeTheWordI = true)
    {
        var newMessage = SanitizeMessageReplaceWords(message.Trim());

        GetRadioKeycodePrefix(source, newMessage, out newMessage, out var prefix);

        // Sanitize it first as it might change the word order
        _sanitizer.TrySanitizeEmoteShorthands(newMessage, source, out newMessage, out emoteStr);

        if (capitalize)
            newMessage = SanitizeMessageCapital(newMessage);
        if (capitalizeTheWordI)
            newMessage = SanitizeMessageCapitalizeTheWordI(newMessage, "i");
        if (punctuate)
            newMessage = SanitizeMessagePeriod(newMessage);

        return prefix + newMessage;
    }

    private string SanitizeInGameOOCMessage(string message)
    {
        var newMessage = message.Trim();
        newMessage = FormattedMessage.EscapeText(newMessage);

        return newMessage;
    }

    public string TransformSpeech(EntityUid sender, string message, LanguagePrototype language)
    {
        if (!language.SpeechOverride.RequireSpeech)
            return message; // Do not apply speech accents if there's no speech involved.

        var ev = new TransformSpeechEvent(sender, message);
        RaiseLocalEvent(ev);

        return ev.Message;
    }


    public bool CheckIgnoreSpeechBlocker(EntityUid sender, bool ignoreBlocker)
    {
        if (ignoreBlocker)
            return ignoreBlocker;

        var ev = new CheckIgnoreSpeechBlockerEvent(sender, ignoreBlocker);
        RaiseLocalEvent(sender, ev, true);

        return ev.IgnoreBlocker;
    }

    private IEnumerable<INetChannel> GetDeadChatClients()
    {
        return Filter.Empty()
            .AddWhereAttachedEntity(HasComp<GhostComponent>)
            .Recipients
            .Union(_adminManager.ActiveAdmins)
            .Select(p => p.Channel);
    }

    private string SanitizeMessagePeriod(string message)
    {
        if (string.IsNullOrEmpty(message))
            return message;
        // Adds a period if the last character is a letter.
        if (char.IsLetter(message[^1]))
            message += ".";
        return message;
    }

    [ValidatePrototypeId<ReplacementAccentPrototype>]
    public const string ChatSanitize_Accent = "chatsanitize";

    public string SanitizeMessageReplaceWords(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;

        var msg = message;

        msg = _wordreplacement.ApplyReplacements(msg, ChatSanitize_Accent);

        return msg;
    }

    #region Backmen-languages

        /// <summary>
    ///     Wraps a message sent by the specified entity into an "x says y" string.
    /// </summary>
    public string WrapPublicMessage(EntityUid source, string name, string message, LanguagePrototype? language = null)
    {
        var wrapId = GetSpeechVerb(source, message).Bold ? "chat-manager-entity-say-bold-wrap-message" : "chat-manager-entity-say-wrap-message";
        return WrapMessage(wrapId, InGameICChatType.Speak, source, name, message, language);
    }

    /// <summary>
    ///     Wraps a message whispered by the specified entity into an "x whispers y" string.
    /// </summary>
    public string WrapWhisperMessage(EntityUid source, LocId defaultWrap, string entityName, string message, LanguagePrototype? language = null)
    {
        return WrapMessage(defaultWrap, InGameICChatType.Whisper, source, entityName, message, language);
    }

    /// <summary>
    ///     Wraps a message sent by the specified entity into the specified wrap string.
    /// </summary>
    public string WrapMessage(LocId wrapId, InGameICChatType chatType, EntityUid source, string entityName, string message, LanguagePrototype? language)
    {
        language ??= _language.GetLanguage(source);
        if (language.SpeechOverride.MessageWrapOverrides.TryGetValue(chatType, out var wrapOverride))
            wrapId = wrapOverride;

        var speech = GetSpeechVerb(source, message);
        var verbId = language.SpeechOverride.SpeechVerbOverrides is { } verbsOverride
            ? _random.Pick(verbsOverride).ToString()
            : _random.Pick(speech.SpeechVerbStrings);

        message = FormattedMessage.EscapeText(message);

        if (language.SpeechOverride.Color is { } colorOverride)
        {
            var color = Color.InterpolateBetween(DefaultSpeakColor, colorOverride, colorOverride.A);
            message = Loc.GetString("chat-manager-wrap-language-color", ("message", message), ("color", color));
        }

        if (
            language.SpeechOverride?.FontId != null ||
            language.SpeechOverride?.FontSize != null
            )
        {
            message = Loc.GetString( chatType == InGameICChatType.Whisper ? "chat-manager-wrap-language-font" : "chat-manager-wrap-language-font-whisper",
                ("message", message),
                ("fontType", language.SpeechOverride.FontId ?? speech.FontId),
                ("fontSize", language.SpeechOverride.FontSize ?? speech.FontSize)
                );
        }


        return Loc.GetString(wrapId,
            ("entityName", entityName),
            ("verb", Loc.GetString(verbId)),
            ("message", message));
    }

    private bool CheckAttachedGrids(EntityUid source, EntityUid receiver)
    {
        if (!TryComp<JointComponent>(Transform(source).GridUid, out var sourceJoints)
            || !TryComp<JointComponent>(Transform(receiver).GridUid, out var receiverJoints))
            return false;

        foreach (var (id, _) in sourceJoints.GetJoints)
        {
            if (receiverJoints.GetJoints.ContainsKey(id))
                return true;
        }

        return false;
    }

    #endregion

    /// <summary>
    ///     Returns list of players and ranges for all players withing some range. Also returns observers with a range of -1.
    /// </summary>
    private Dictionary<ICommonSession, ICChatRecipientData> GetRecipients(EntityUid source, float voiceGetRange)
    {
        // TODO proper speech occlusion

        var recipients = new Dictionary<ICommonSession, ICChatRecipientData>();
        var ghostHearing = GetEntityQuery<GhostHearingComponent>();
        var xforms = GetEntityQuery<TransformComponent>();

        var transformSource = xforms.GetComponent(source);
        var sourceMapId = transformSource.MapID;
        var sourceCoords = transformSource.Coordinates;

        foreach (var player in _playerManager.Sessions)
        {
            if (player.AttachedEntity is not { Valid: true } playerEntity)
                continue;

            var transformEntity = xforms.GetComponent(playerEntity);

            if (transformEntity.MapID != sourceMapId)
                continue;

            var observer = ghostHearing.HasComponent(playerEntity);

            // even if they are a ghost hearer, in some situations we still need the range
            if (sourceCoords.TryDistance(EntityManager, transformEntity.Coordinates, out var distance) && distance < voiceGetRange)
            {
                recipients.Add(player, new ICChatRecipientData(distance, observer));
                continue;
            }

            if (observer)
                recipients.Add(player, new ICChatRecipientData(-1, true));
        }

        RaiseLocalEvent(new ExpandICChatRecipientsEvent(source, voiceGetRange, recipients));
        return recipients;
    }

    public readonly record struct ICChatRecipientData(float Range, bool Observer, bool? HideChatOverride = null)
    {
    }

    public string ObfuscateMessageReadability(string message, float chance=0.2f)
    {
        var modifiedMessage = new StringBuilder(message);

        for (var i = 0; i < message.Length; i++)
        {
            if (char.IsWhiteSpace((modifiedMessage[i])))
            {
                continue;
            }

            if (_random.Prob(1 - chance))
            {
                modifiedMessage[i] = '~';
            }
        }

        return modifiedMessage.ToString();
    }

    public string BuildGibberishString(IReadOnlyList<char> charOptions, int length)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < length; i++)
        {
            sb.Append(_random.Pick(charOptions));
        }
        return sb.ToString();
    }

    #endregion
}

/// <summary>
///     This event is raised before chat messages are sent out to clients. This enables some systems to send the chat
///     messages to otherwise out-of view entities (e.g. for multiple viewports from cameras).
/// </summary>
public record ExpandICChatRecipientsEvent(EntityUid Source, float VoiceRange, Dictionary<ICommonSession, ChatSystem.ICChatRecipientData> Recipients)
{
}

/// <summary>
///     Raised broadcast in order to transform speech.transmit
/// </summary>
public sealed class TransformSpeechEvent : EntityEventArgs
{
    public EntityUid Sender;
    public string Message;

    public TransformSpeechEvent(EntityUid sender, string message)
    {
        Sender = sender;
        Message = message;
    }
}

public sealed class CheckIgnoreSpeechBlockerEvent : EntityEventArgs
{
    public EntityUid Sender;
    public bool IgnoreBlocker;

    public CheckIgnoreSpeechBlockerEvent(EntityUid sender, bool ignoreBlocker)
    {
        Sender = sender;
        IgnoreBlocker = ignoreBlocker;
    }
}

/// <summary>
///     Raised on an entity when it speaks, either through 'say' or 'whisper'.
/// </summary>
public sealed class EntitySpokeEvent : EntityEventArgs
{
    public readonly EntityUid Source;
    public readonly string Message;
    public readonly string OriginalMessage;
    public readonly string? ObfuscatedMessage; // not null if this was a whisper
    public readonly LanguagePrototype? Language;

    /// <summary>
    ///     If the entity was trying to speak into a radio, this was the channel they were trying to access. If a radio
    ///     message gets sent on this channel, this should be set to null to prevent duplicate messages.
    /// </summary>
    public RadioChannelPrototype? Channel;

    public EntitySpokeEvent(EntityUid source, string message, string originalMessage, RadioChannelPrototype? channel = null, string? obfuscatedMessage = null, LanguagePrototype? language = null)
    {
        Source = source;
        Message = message;
        OriginalMessage = originalMessage; // Corvax-TTS: Spec symbol sanitize
        Channel = channel;
        ObfuscatedMessage = obfuscatedMessage;
        Language = language;
    }
}

/// <summary>
///     InGame OOC chat is for chat that is specifically ingame (not lobby) but is OOC, like deadchat or LOOC.
/// </summary>
public enum InGameOOCChatType : byte
{
    Looc,
    Dead
}

/// <summary>
///     Controls transmission of chat.
/// </summary>
public enum ChatTransmitRange : byte
{
    /// Acts normal, ghosts can hear across the map, etc.
    Normal,
    /// Normal but ghosts are still range-limited.
    GhostRangeLimit,
    /// Hidden from the chat window.
    HideChat,
    /// Ghosts can't hear or see it at all. Regular players can if in-range.
    NoGhosts
}
