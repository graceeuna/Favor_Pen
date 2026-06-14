using System;
using System.IO;
using System.Text.Json;

namespace ScreenPenPortable.Settings;

/// <summary>
/// <see cref="AppSettings"/> 를 실행파일 옆 <c>settings.json</c> 에 읽고 쓴다.
/// 무설치(Portable) 정책상 설정은 앱 폴더에 보관한다.
///
/// 설계 원칙: 설정 입출력 실패가 앱을 죽여서는 안 된다.
///  - <see cref="Load"/> 는 파일이 없거나 손상되어도 예외 대신 기본 설정을 반환한다.
///  - <see cref="Save"/> 는 IO 예외를 삼키고, 임시파일→교체 방식으로 원자적 저장을 시도한다.
/// </summary>
public static class SettingsStore
{
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// 설정을 로드한다. 파일이 없거나 역직렬화에 실패하면 기본값(<c>new AppSettings()</c>)을 반환한다.
    /// 절대 예외를 던지지 않는다.
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new AppSettings();

            string json = File.ReadAllText(FilePath);
            AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, s_options);
            return loaded ?? new AppSettings();
        }
        catch
        {
            // 손상된 파일/권한 문제/JSON 파싱 실패 등 → 기본값으로 폴백.
            return new AppSettings();
        }
    }

    /// <summary>
    /// 설정을 저장한다. 임시파일에 먼저 쓴 뒤 교체하여 부분 기록(half-written)을 방지한다.
    /// IO 예외는 삼킨다(throw 금지).
    /// </summary>
    public static void Save(AppSettings s)
    {
        try
        {
            string json = JsonSerializer.Serialize(s, s_options);
            string tempPath = FilePath + ".tmp";

            File.WriteAllText(tempPath, json);

            // 임시파일 → 최종파일 원자적 교체.
            // 대상이 없으면 Replace 가 실패하므로 Move 로 폴백.
            if (File.Exists(FilePath))
                File.Replace(tempPath, FilePath, destinationBackupFileName: null);
            else
                File.Move(tempPath, FilePath);
        }
        catch
        {
            // 디스크 가득참/권한/잠금 등 → 조용히 무시.
        }
    }
}
