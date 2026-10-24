// Shared by the agent and the installer - bump Version here for a release.
namespace BloomPrintAgent
{
    static class AppInfo
    {
        public const string Name = "Bloom Print Agent";
        public const string Version = "1.0.0";
        public const string Publisher = "BloomTech";
        public const string ExeName = "BloomPrintAgent.exe";

        // named events the running agent listens on (see Program.cs)
        public const string ShowEventName = "Local\\BloomPrintAgent.Show";
        public const string ExitEventName = "Local\\BloomPrintAgent.Exit";
    }
}
