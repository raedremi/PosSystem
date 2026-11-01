using RasidSync.Services;

namespace RasidSync;

public partial class ReceiveCounterForm : Form
{
    public long FirstEvent => (long)firstEvent.Value;
    public ReceiveCounterForm(ReceiveCounterSnapshot state)
    {
        InitializeComponent();
        serverValue.Text=state.ServerMaximum.ToString();
        localValue.Text=(state.LocalCursor+1).ToString();
        firstEvent.Maximum=state.ServerMaximum+1;
        firstEvent.Value=Math.Clamp(state.LocalCursor+1,1,state.ServerMaximum+1);
    }
}
