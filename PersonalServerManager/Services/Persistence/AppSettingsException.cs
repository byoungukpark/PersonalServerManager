namespace PersonalServerManager.Services.Persistence;

/// <summary>앱 설정 JSON을 읽거나 보존할 수 없을 때의 안전한 오류 메시지다.</summary>
public sealed class AppSettingsException(string message) : Exception(message);
