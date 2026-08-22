using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FSharp.Core;
using Microsoft.Win32;
using MudBlazor;
using MudBlazor.Services;
using MyDogsbody.UI.Types;

namespace MyDogsbody
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            var serviceCollection = new ServiceCollection();
            serviceCollection.AddWpfBlazorWebView();
            serviceCollection.AddMudServices(config =>
            {
                config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.TopRight;
                config.SnackbarConfiguration.PreventDuplicates = false;
                config.SnackbarConfiguration.NewestOnTop = false;
                config.SnackbarConfiguration.ShowCloseIcon = true;
                config.SnackbarConfiguration.VisibleStateDuration = 5000;
                config.SnackbarConfiguration.HideTransitionDuration = 500;
                config.SnackbarConfiguration.ShowTransitionDuration = 500;
                config.SnackbarConfiguration.SnackbarVariant = Variant.Filled;
            });
            // Every application service is registered by the F# composition root, so this
            // file states that services exist without stating how they are built.
            MyDogsbody.Startup.Startup.registerServices(serviceCollection);

            // The one exception: the domain never opens dialogs, so the folder picker the
            // Thunderbird accounts page needs is a UI-level service the host supplies directly,
            // not something the composition root can build. This is the only change to this
            // file in the whole invoice-to-calendar series - see CLAUDE-project.md -> "The
            // folder picker is not a domain dependency".
            // MyDogsbody.UI.Types.FolderPicker is an F# type abbreviation for
            // `unit -> string option` (Microsoft.FSharp.Core.FSharpFunc<Unit,FSharpOption<string>>)
            // - abbreviations are erased at compile time, so there is no CLR type named
            // FolderPicker to reference from C#. Registering under the erased type is what makes
            // the F# side's `html.inject (fun (picker: FolderPicker) -> ...)` resolve it: DI
            // matches by CLR type, and FolderPicker erases to exactly this one.
            var folderPicker = FolderPickerInterop.ofFunc(new Func<string?>(() =>
            {
                var dialog = new OpenFolderDialog();
                return dialog.ShowDialog() == true ? dialog.FolderName : null;
            })!);
            serviceCollection.AddSingleton<FSharpFunc<Unit, FSharpOption<string>>>(folderPicker);

            Resources.Add("services", serviceCollection.BuildServiceProvider());
        }
    }
}