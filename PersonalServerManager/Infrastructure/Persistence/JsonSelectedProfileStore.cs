using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using PersonalServerManager.Application.Contracts;
using PersonalServerManager.Application.Services;

namespace PersonalServerManager.Infrastructure.Persistence;

/// <summary>통합 설정 JSON에서 마지막 선택 프로필명만 읽고 다른 설정 필드는 보존한다.</summary>
public sealed class JsonSelectedProfileStore : ISelectedProfileStore
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private readonly string _path;

    public JsonSelectedProfileStore()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new AppSettingsException("앱 설정 저장 위치를 찾지 못했습니다.");
        }

        _path = Path.Combine(localAppData, "PersonalServerManager", "settings.json");
    }

    public async Task<string?> LoadAsync(CancellationToken cancellationToken)
    {
        await _fileGate.WaitAsync(cancellationToken);
        try
        {
            var root = await ReadRootAsync(cancellationToken);
            if (root is null)
            {
                return null;
            }

            var appSettings = GetAppSettings(root);
            var selected = appSettings?["lastSelectedProfileName"];
            return selected is null ? null : selected.GetValue<string>();
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
            _fileGate.Release();
        }
    }

    public async Task SaveAsync(string? profileName, CancellationToken cancellationToken)
    {
        await _fileGate.WaitAsync(cancellationToken);
        string? tempPath = null;
        try
        {
            var root = await ReadRootAsync(cancellationToken) ?? new JsonObject
            {
                ["schemaVersion"] = CurrentSchemaVersion,
                ["appSettings"] = new JsonObject()
            };

            var appSettings = GetAppSettings(root);
            if (appSettings is null)
            {
                appSettings = new JsonObject();
                root["appSettings"] = appSettings;
            }

            appSettings["lastSelectedProfileName"] = profileName;
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            tempPath = Path.Combine(directory, $"settings.{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(tempPath, root.ToJsonString(WriteOptions), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(_path))
            {
                File.Replace(tempPath, _path, null);
            }
            else
            {
                File.Move(tempPath, _path);
            }

            tempPath = null;
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
            try
            {
                if (tempPath is not null && File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 임시 파일 삭제 실패는 다음 저장을 막지 않는다.
            }
            finally
            {
                _fileGate.Release();
            }
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

        _ = GetAppSettings(root);
        return root;
    }

    private static JsonObject? GetAppSettings(JsonObject root)
    {
        if (root["appSettings"] is null)
        {
            return null;
        }

        if (root["appSettings"] is not JsonObject appSettings)
        {
            throw new AppSettingsException("앱 설정 JSON의 appSettings 형식이 올바르지 않습니다.");
        }

        return appSettings;
    }
}
