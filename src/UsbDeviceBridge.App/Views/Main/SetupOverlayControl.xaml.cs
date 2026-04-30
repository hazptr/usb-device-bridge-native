using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Ellipse = System.Windows.Shapes.Ellipse;
using Grid = System.Windows.Controls.Grid;
using Rectangle = System.Windows.Shapes.Rectangle;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace UsbDeviceBridge.App.Views.Main;

public partial class SetupOverlayControl : UserControl
{
    public SetupOverlayControl()
    {
        InitializeComponent();
    }

    public Grid StepOnePanel => SetupStepOnePanel;

    public Grid StepTwoPanelPrereq => SetupStepTwoPanelPrereq;

    public Grid StepThreePanelDistro => SetupStepThreePanelDistro;

    public Grid StepFourPanel => SetupStepFourPanel;

    public Grid DistroSelectionView => SetupDistroSelectionView;

    public Grid DistroLogView => SetupDistroLogView;

    public StackPanel DistroCheckboxes => SetupDistroCheckboxes;

    public StackPanel PrerequisitesStatus => SetupPrerequisitesStatus;

    public TextBox InstallLogText => SetupInstallLogText;

    public Button InstallPackagesButton => SetupInstallPackagesButton;

    public Button InstallStopButton => SetupInstallStopButton;

    public Button InstallStartOverButton => SetupInstallStartOverButton;

    public Button BackButton => SetupBackButton;

    public Button NextButton => SetupNextButton;

    public Button DarkCard => SetupDarkCard;

    public Button LightCard => SetupLightCard;

    public TextBlock DarkLabel => SetupDarkLabel;

    public TextBlock LightLabel => SetupLightLabel;

    public Rectangle DarkSwatch1 => SetupDarkSwatch1;

    public Rectangle DarkSwatch2 => SetupDarkSwatch2;

    public Rectangle DarkSwatch3 => SetupDarkSwatch3;

    public Rectangle LightSwatch1 => SetupLightSwatch1;

    public Rectangle LightSwatch2 => SetupLightSwatch2;

    public Rectangle LightSwatch3 => SetupLightSwatch3;

    public Ellipse DotOne => SetupDotOne;

    public Ellipse DotTwo => SetupDotTwo;

    public Ellipse DotThree => SetupDotThree;

    public Ellipse DotFour => SetupDotFour;

    public CheckBox EnableTray => SetupEnableTray;

    public CheckBox StartMinimized => SetupStartMinimized;

    public CheckBox AutoRefresh => SetupAutoRefresh;

    public CheckBox AutoUpdate => SetupAutoUpdate;
}
