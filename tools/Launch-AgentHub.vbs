' AGENT HUB - hidden launcher for the elevated scheduled task
' Created by tools\Install-AgentHubShortcut.ps1
' Runs the "AgentHub" scheduled task without showing a console window;
' the task itself runs with highest privileges, so no UAC prompt appears.
CreateObject("Wscript.Shell").Run "schtasks /run /tn ""AgentHub""", 0, False
