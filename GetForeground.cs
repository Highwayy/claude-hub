// Compile this as: csc /target:library /out:GetForeground.dll GetForeground.cs
// Then use from PowerShell: [GetForeground.Window]::GetInfo()

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace GetForeground
{
    public class Window
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        public static string GetInfo()
        {
            IntPtr hWnd = GetForegroundWindow();
            StringBuilder sb = new StringBuilder(256);
            GetClassName(hWnd, sb, 256);
            return hWnd.ToInt64() + "|" + sb.ToString();
        }
    }
}