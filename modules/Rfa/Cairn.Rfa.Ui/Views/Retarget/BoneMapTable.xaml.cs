using System.Windows.Controls;

namespace Cairn.Rfa.Ui.Views.Retarget;

/// <summary>The bone map table (DataContext: <c>BoneMapEditorViewModel</c>); shared by the retarget and glTF import dialogs.</summary>
public partial class BoneMapTable : UserControl
{
    public BoneMapTable() => InitializeComponent();
}
