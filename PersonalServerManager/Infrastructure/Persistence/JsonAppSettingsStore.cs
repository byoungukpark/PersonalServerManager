using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PersonalServerManager.Application.Contracts;
using PersonalServerManager.Application.Services;
using PersonalServerManager.Domain.Models;
using PersonalServerManager.Domain.Rules;

namespace PersonalServerManager.Infrastructure.Persistence;

/// <summary>프로필 선택과 서비스 그룹을 하나의 사용자별 JSON 파일에 안전하게 저장한다.</summary>
public sealed class JsonAppSettingsStore : ISelectedProfileStore, IServiceGroupStore
{
    private const int CurrentSchemaVersion = 1;
    private static readonly SemaphoreSlim FileGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _path;

    public JsonAppSettingsStore(string? path = null)
    {
        if (path is not null)
        {
            _path = path;
            return;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new AppSettingsException("앱 설정 저장 위치를 찾지 못했습니다.");
        }

        _path = Path.Combine(localAppData, "PersonalServerManager", "settings.json");
    }

    public async Task<string?> LoadAsync(CancellationToken cancellationToken)
    {
        await FileGate.WaitAsync(cancellationToken);
        try
        {
            var root = await ReadRootAsync(cancellationToken);
            return root is null ? null : (string?)root["appSettings"]?["lastSelectedProfileName"]?.GetValue<string>();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AppSettingsException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new AppSettingsException("앱 설정 JSON이 손상되었거나 읽을 수 없습니다. 파일을 확인해 주세요.");
        }
        finally
        {
            FileGate.Release();
        }
    }

    public async Task<IReadOnlyList<ServiceGroup>> LoadGroupsAsync(CancellationToken cancellationToken)
    {
        await FileGate.WaitAsync(cancellationToken);
        try
        {
            var root = await ReadRootAsync(cancellationToken);
            return root is null ? Array.Empty<ServiceGroup>() : ReadGroups(root);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AppSettingsException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new AppSettingsException("서비스 그룹 설정을 읽지 못했습니다. 설정 파일을 확인해 주세요.");
        }
        finally
        {
            FileGate.Release();
        }
    }

    public async Task SaveAsync(string? profileName, CancellationToken cancellationToken)
    {
        await FileGate.WaitAsync(cancellationToken);
        try
        {
            var root = await ReadRootAsync(cancellationToken) ?? NewRoot();
            var appSettings = root["appSettings"] as JsonObject;
            if (appSettings is null)
            {
                appSettings = new JsonObject();
                root["appSettings"] = appSettings;
            }

            appSettings["lastSelectedProfileName"] = profileName;
            await WriteAtomicallyAsync(root, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AppSettingsException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new AppSettingsException("마지막 선택 프로필을 앱 설정에 저장하지 못했습니다. 설정 파일을 확인해 주세요.");
        }
        finally
        {
            FileGate.Release();
        }
    }

    public async Task AddGroupAsync(ServiceGroup group, CancellationToken cancellationToken)
    {
        GroupRules.Validate(group);
        await FileGate.WaitAsync(cancellationToken);
        try
        {
            var root = await ReadRootAsync(cancellationToken) ?? NewRoot();
            GroupRules.EnsureUnique(group, ReadGroups(root));

            var groups = root["groups"] as JsonArray;
            if (groups is null)
            {
                groups = new JsonArray();
                root["groups"] = groups;
            }

            groups.Add(JsonSerializer.SerializeToNode(group, JsonOptions));
            await WriteAtomicallyAsync(root, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is AppSettingsException or ServiceGroupException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new AppSettingsException("서비스 그룹을 앱 설정에 저장하지 못했습니다. 설정 파일을 확인해 주세요.");
        }
        finally
        {
            FileGate.Release();
        }
    }

    private async Task<JsonObject?> ReadRootAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(_path, cancellationToken);
        var root = JsonNode.Parse(json) as JsonObject;
        if (root is null || root["schemaVersion"] is not JsonValue version ||
            !version.TryGetValue<int>(out var schemaVersion) || schemaVersion != CurrentSchemaVersion)
        {
            throw new AppSettingsException("앱 설정 JSON 형식 또는 버전이 올바르지 않습니다. 파일을 확인해 주세요.");
        }

        if (root.ContainsKey("appSettings") && root["appSettings"] is not JsonObject)
        {
            throw new AppSettingsException("앱 설정 JSON의 appSettings 형식이 올바르지 않습니다.");
        }

        var selected = root["appSettings"]?["lastSelectedProfileName"];
        if (selected is not null && (selected is not JsonValue value || !value.TryGetValue<string>(out _)))
        {
            throw new AppSettingsException("앱 설정 JSON의 선택 프로필 이름이 올바르지 않습니다.");
        }

        _ = ReadGroups(root);
        return root;
    }

    private static IReadOnlyList<ServiceGroup> ReadGroups(JsonObject root)
    {
        if (!root.ContainsKey("groups"))
        {
            return Array.Empty<ServiceGroup>();
        }

        if (root["groups"] is not JsonArray array)
        {
            throw new AppSettingsException("앱 설정 JSON의 groups 형식이 올바르지 않습니다.");
        }

        var groups = new List<ServiceGroup>(array.Count);
        try
        {
            foreach (var node in array)
            {
                var group = node?.Deserialize<ServiceGroup>(JsonOptions) ??
                    throw new ServiceGroupException("서비스 그룹 항목이 비어 있습니다.");
                GroupRules.Validate(group);
                GroupRules.EnsureUnique(group, groups);
                groups.Add(group);
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ServiceGroupException)
        {
            throw new AppSettingsException("서비스 그룹 설정이 손상되었거나 필수 값이 올바르지 않습니다. 파일을 확인해 주세요.");
        }

        return groups;
    }

    private static JsonObject NewRoot() => new()
    {
        ["schemaVersion"] = CurrentSchemaVersion,
        ["appSettings"] = new JsonObject(),
        ["groups"] = new JsonArray()
    };

    /// <summary>완성된 임시 파일을 같은 폴더의 설정 파일로 교체해 부분 쓰기를 피한다.</summary>
    private async Task WriteAtomicallyAsync(JsonObject root, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new AppSettingsException("앱 설정 저장 경로가 올바르지 않습니다.");
        }

        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $"settings.{Guid.NewGuid():N}.tmp");
        var backupPath = Path.Combine(directory, $"settings.{Guid.NewGuid():N}.bak");
        try
        {
            await File.WriteAllTextAsync(tempPath, root.ToJsonString(JsonOptions), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_path))
            {
                File.Replace(tempPath, _path, backupPath);
            }
            else
            {
                File.Move(tempPath, _path);
            }
        }
        finally
        {
            TryDeleteTemporaryFile(tempPath);
            TryDeleteTemporaryFile(backupPath);
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 저장 결과와 무관한 임시 파일 정리 실패로 추가 오류를 내지 않는다.
        }
    }
}
