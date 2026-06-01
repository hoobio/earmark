using System.Collections.Generic;
using System.IO;

namespace Earmark.Core.Models;

public static class ProcessRollup
{
    private const string WebViewExecutableName = "msedgewebview2.exe";
    private const int MaxAncestorDepth = 16;

    public static uint ResolveHostedOwnerProcessId(
        uint processId,
        Func<uint, (uint ParentProcessId, string ProcessName, string ExecutablePath)> getProcessInfo)
    {
        ArgumentNullException.ThrowIfNull(getProcessInfo);

        if (processId == 0)
        {
            return 0;
        }

        var currentInfo = getProcessInfo(processId);
        if (!IsHostedWebView(currentInfo.ProcessName, currentInfo.ExecutablePath))
        {
            return processId;
        }

        var currentPid = processId;
        var seen = new HashSet<uint> { processId };

        for (var depth = 0; depth < MaxAncestorDepth; depth++)
        {
            var parentPid = currentInfo.ParentProcessId;
            if (parentPid == 0 || !seen.Add(parentPid))
            {
                return processId;
            }

            var parentInfo = getProcessInfo(parentPid);
            if (string.IsNullOrEmpty(parentInfo.ProcessName) && string.IsNullOrEmpty(parentInfo.ExecutablePath))
            {
                return processId;
            }

            if (!IsHostedWebView(parentInfo.ProcessName, parentInfo.ExecutablePath))
            {
                return parentPid;
            }

            currentPid = parentPid;
            currentInfo = parentInfo;
        }

        return currentPid;
    }

    public static bool IsHostedWebView(string processName, string executablePath)
    {
        if (!string.IsNullOrEmpty(executablePath) &&
            string.Equals(Path.GetFileName(executablePath), WebViewExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(processName, Path.GetFileNameWithoutExtension(WebViewExecutableName), StringComparison.OrdinalIgnoreCase);
    }
}
