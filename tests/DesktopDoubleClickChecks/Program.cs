using System;
using LinkLauncher.Services;

internal static class Program
{
    private const int RoleSystemList = 0x21;
    private const int RoleSystemListItem = 0x22;

    private static int Main()
    {
        try
        {
            CheckClickPairAndUpCompletion();
            CheckClickTimeoutAndRearm();
            CheckClickPositionAndWraparound();
            CheckDragCancellation();
            CheckDesktopRoleClassification();
            Console.WriteLine("PASS DesktopDoubleClickChecks (5 checks)");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL " + error.Message);
            return 1;
        }
    }

    private static void CheckClickPairAndUpCompletion()
    {
        var state = new DesktopDoubleClickState();
        Require(!state.Down(100, 200, 1000, 500, 4, 4), "1回目DOWNを候補として記録できません。");
        Require(!state.Up(100, 200), "1回目UPをダブルクリック完了と扱っています。");
        Require(state.Down(102, 198, 1200, 500, 4, 4), "許容範囲内の2回目DOWNを候補として認識できません。");
        Require(state.Up(102, 198), "2回目UP後に候補が完了しません。");
        Require(!state.Up(102, 198), "完了済み候補を二重に発火できてしまいます。");
    }

    private static void CheckClickTimeoutAndRearm()
    {
        var state = new DesktopDoubleClickState();
        Require(!state.Down(0, 0, 100, 250, 4, 4), "1回目DOWNを記録できません。");
        Require(!state.Up(0, 0), "1回目UPで誤発火しています。");
        Require(!state.Down(0, 0, 351, 250, 4, 4), "時間切れのクリックを2回目として扱っています。");
        Require(!state.Up(0, 0), "再登録した1回目UPで発火しています。");
        Require(state.Down(0, 0, 400, 250, 4, 4), "時間切れ後の新しいペアを再登録できません。");
        Require(state.Up(0, 0), "再登録したペアを完了できません。");
    }

    private static void CheckClickPositionAndWraparound()
    {
        var state = new DesktopDoubleClickState();
        Require(!state.Down(10, 10, 100, 500, 3, 2), "位置試験の1回目DOWNを記録できません。");
        Require(!state.Up(10, 10), "位置試験の1回目UPで発火しています。");
        Require(!state.Down(14, 10, 200, 500, 3, 2), "許容矩形外のクリックを2回目として扱っています。");
        Require(!state.Up(14, 10), "位置外の再登録クリックで発火しています。");

        state.Cancel();
        Require(!state.Down(10, 10, uint.MaxValue - 100, 500, 3, 2), "tick wraparound試験の1回目を記録できません。");
        Require(!state.Up(10, 10), "tick wraparound試験の1回目UPで発火しています。");
        Require(state.Down(10, 10, 50, 500, 3, 2), "32-bit tick wraparoundを越えたペアを認識できません。");
        Require(state.Up(10, 10), "tick wraparoundのペアを完了できません。");
    }

    private static void CheckDragCancellation()
    {
        var state = new DesktopDoubleClickState();
        Require(!state.Down(50, 50, 1000, 500, 4, 4), "ドラッグ試験の1回目DOWNを記録できません。");
        state.Move(55, 50, leftButtonDown: true);
        Require(!state.Up(55, 50), "許容範囲外の1回目ドラッグが候補を残しています。");
        Require(!state.Down(50, 50, 1200, 500, 4, 4), "ドラッグ後の新しいクリックを2回目と扱っています。");
        Require(!state.Up(50, 50), "ドラッグ後の新しい1回目UPで発火しています。");

        Require(state.Down(51, 50, 1300, 500, 4, 4), "ドラッグ後に新しいクリックペアを認識できません。");
        state.Move(51, 50, leftButtonDown: false);
        Require(state.Up(51, 50), "button-up後の通常移動がクリックペアを壊しています。");
    }

    private static void CheckDesktopRoleClassification()
    {
        var nonDesktop = new FakeDesktopDoubleClickApi(false, true, RoleSystemList);
        Require(!DesktopDoubleClick.IsBlankAt(20, 30, nonDesktop), "Shell以外のwindowをdesktop空白と扱っています。");
        Require(!nonDesktop.RoleQueried, "Shell以外のwindowにMSAA照会しています。");

        var desktopItem = new FakeDesktopDoubleClickApi(true, true, RoleSystemListItem);
        Require(!DesktopDoubleClick.IsBlankAt(20, 30, desktopItem), "desktop iconを空白と扱っています。");

        var desktopWhitespace = new FakeDesktopDoubleClickApi(true, true, RoleSystemList);
        Require(DesktopDoubleClick.IsBlankAt(20, 30, desktopWhitespace), "desktop list roleの空白を認識できません。");

        var failedAccessibility = new FakeDesktopDoubleClickApi(true, false, 0);
        Require(!DesktopDoubleClick.IsBlankAt(20, 30, failedAccessibility), "MSAA照会失敗を空白と扱っています。");

        var unknownRole = new FakeDesktopDoubleClickApi(true, true, 0x0F);
        Require(!DesktopDoubleClick.IsBlankAt(20, 30, unknownRole), "未知のMSAA roleを空白と扱っています。");

        var throwingProbe = new FakeDesktopDoubleClickApi(true, true, RoleSystemList, throwOnRole: true);
        Require(!DesktopDoubleClick.IsBlankAt(20, 30, throwingProbe), "MSAA例外を空白と扱っています。");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeDesktopDoubleClickApi : IDesktopDoubleClickApi
    {
        private readonly bool _isDesktop;
        private readonly bool _hasRole;
        private readonly int _role;
        private readonly bool _throwOnRole;

        public FakeDesktopDoubleClickApi(bool isDesktop, bool hasRole, int role, bool throwOnRole = false)
        {
            _isDesktop = isDesktop;
            _hasRole = hasRole;
            _role = role;
            _throwOnRole = throwOnRole;
        }

        public bool RoleQueried { get; private set; }

        public bool IsDesktopViewAtPhysicalPoint(int x, int y) => _isDesktop;

        public bool TryGetAccessibleRoleAtPhysicalPoint(int x, int y, out int role)
        {
            RoleQueried = true;
            if (_throwOnRole) throw new InvalidOperationException("fake MSAA failure");
            role = _role;
            return _hasRole;
        }
    }
}
