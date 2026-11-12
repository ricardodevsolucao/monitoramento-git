using System.Text.Json.Serialization;
using Monitoramento.Core.Models;

namespace Monitoramento.Core.Bridge;

/// <summary>Serialização source-generated em camelCase para mensagens da ponte e leitura da configuração.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    NumberHandling = JsonNumberHandling.Strict,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(Snapshot))]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(BridgeRequest))]
[JsonSerializable(typeof(KeyPayload))]
[JsonSerializable(typeof(IdPayload))]
[JsonSerializable(typeof(PidPayload))]
[JsonSerializable(typeof(KillProcessPayload))]
[JsonSerializable(typeof(PathPayload))]
[JsonSerializable(typeof(KeysPayload))]
[JsonSerializable(typeof(SavePayload))]
[JsonSerializable(typeof(SettingsPayload))]
[JsonSerializable(typeof(ReadyData))]
[JsonSerializable(typeof(FavoritesData))]
[JsonSerializable(typeof(IconsData))]
[JsonSerializable(typeof(SavedData))]
[JsonSerializable(typeof(DraftData))]
[JsonSerializable(typeof(StateData))]
[JsonSerializable(typeof(KilledData))]
[JsonSerializable(typeof(SettingsData))]
[JsonSerializable(typeof(EmptyData))]
[JsonSerializable(typeof(GitDetailsPayload))]
[JsonSerializable(typeof(GitDetailsData))]
[JsonSerializable(typeof(GitRepoPayload))]
[JsonSerializable(typeof(GitPathsPayload))]
[JsonSerializable(typeof(GitCommitPayload))]
[JsonSerializable(typeof(GitCheckoutPayload))]
[JsonSerializable(typeof(GitMergePayload))]
[JsonSerializable(typeof(GitActionData))]
[JsonSerializable(typeof(SaveGitProjectPayload))]
[JsonSerializable(typeof(GitProjectDraftData))]
[JsonSerializable(typeof(GitProjectData))]
[JsonSerializable(typeof(SetGitWorkspacePayload))]
[JsonSerializable(typeof(GitWorkspaceData))]
[JsonSerializable(typeof(FolderPayload))]
[JsonSerializable(typeof(ProjectScriptsData))]
[JsonSerializable(typeof(TermOpenPayload))]
[JsonSerializable(typeof(TermRunSavedInPayload))]
[JsonSerializable(typeof(GitWorktreeAddPayload))]
[JsonSerializable(typeof(GitWorktreeAddData))]
[JsonSerializable(typeof(GitWorktreeRemovePayload))]
[JsonSerializable(typeof(TermInputPayload))]
[JsonSerializable(typeof(TermResizePayload))]
[JsonSerializable(typeof(TerminalData))]
[JsonSerializable(typeof(TermAttachData))]
[JsonSerializable(typeof(TermOutputPayload))]
[JsonSerializable(typeof(TermExitPayload))]
internal sealed partial class MonitorJsonContext : JsonSerializerContext;

/// <summary>Mesmo formato, indentado, para o arquivo de configuração (legível à mão).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(AppConfig))]
internal sealed partial class ConfigFileJsonContext : JsonSerializerContext;
