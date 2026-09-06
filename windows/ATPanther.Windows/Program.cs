using System.Windows.Forms;
using ATPanther.Windows;

ApplicationConfiguration.Initialize();
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

var app = new PantherApp();
Application.Run(app);
