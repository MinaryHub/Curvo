using System.Runtime.InteropServices;

namespace Curvo.Interop;

/// <summary>
/// 지금 실행 중인 프로세스가 MSIX 패키지 안에서 도는지 알려준다.
/// <para>
/// 같은 바이너리가 단일 exe 와 MSIX 두 경로로 배포되므로, 채널마다 달라지는 동작은
/// 빌드를 나누지 않고 실행 시점에 판단한다.
/// </para>
/// <list type="bullet">
/// <item>패키지 설치 폴더는 읽기 전용이라 자기 자신을 교체할 수 없다 → 자체 업데이트를 끈다
///       (스토어가 갱신한다).</item>
/// <item>로그온 자동 실행은 HKCU Run 대신 매니페스트의 windows.startupTask 를 쓴다.</item>
/// </list>
/// </summary>
internal static class PackageContext
{
    /// <summary>패키지가 아닐 때 GetCurrentPackageFullName 이 돌려주는 코드.</summary>
    private const int AppModelErrorNoPackage = 15700;

    private static readonly Lazy<bool> Packaged = new(Detect);

    /// <summary>MSIX 패키지로 실행 중인지.</summary>
    public static bool IsPackaged => Packaged.Value;

    private static bool Detect()
    {
        try
        {
            int length = 0;
            // 길이가 0 이므로 패키지일 때는 ERROR_INSUFFICIENT_BUFFER(122) 가 온다.
            // NO_PACKAGE 가 아니라는 것만으로 패키지 안이라는 뜻이다.
            int result = GetCurrentPackageFullName(ref length, null);
            return result != AppModelErrorNoPackage;
        }
        catch (Exception)
        {
            // 이 API 는 Windows 8 이상에 있다. 실패하면 단일 exe 동작을 기본값으로 둔다.
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);
}
