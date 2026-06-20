using System.Runtime.Versioning;
using System.Security.Principal;

namespace ProcessFileMonitor.Core
{
    [SupportedOSPlatform("windows")]
    public static class WindowsPrivilegeHelper
    {
        public static bool IsAdministrator()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
