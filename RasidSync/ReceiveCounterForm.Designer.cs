namespace RasidSync;

partial class ReceiveCounterForm
{
    private System.ComponentModel.IContainer? components;
    private Label serverValue=null!,localValue=null!;
    private NumericUpDown firstEvent=null!;
    protected override void Dispose(bool disposing)
    { if(disposing)components?.Dispose(); base.Dispose(disposing); }
    private void InitializeComponent()
    {
        var layout=new TableLayoutPanel();
        var serverLabel=new Label(); var localLabel=new Label(); var startLabel=new Label();
        var note=new Label(); var buttons=new FlowLayoutPanel(); var save=new Button(); var cancel=new Button();
        serverValue=new Label();localValue=new Label();firstEvent=new NumericUpDown();
        SuspendLayout();
        Text="عداد الاستقبال"; Font=new Font("Segoe UI",10F); ClientSize=new Size(480,290);
        FormBorderStyle=FormBorderStyle.FixedDialog; MaximizeBox=false; MinimizeBox=false;
        StartPosition=FormStartPosition.CenterParent; RightToLeft=RightToLeft.Yes; RightToLeftLayout=true;
        layout.Dock=DockStyle.Fill;layout.Padding=new Padding(18);layout.ColumnCount=2;layout.RowCount=5;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,62));layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,38));
        for(int i=0;i<3;i++)layout.RowStyles.Add(new RowStyle(SizeType.Absolute,40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,45));
        serverLabel.Text="آخر حركة على السيرفر";localLabel.Text="الاستقبال يبدأ حاليًا من";startLabel.Text="البداية التي تريدها";
        foreach(var label in new[]{serverLabel,localLabel,startLabel,serverValue,localValue}) {label.Dock=DockStyle.Fill;label.TextAlign=ContentAlignment.MiddleLeft;}
        serverValue.ForeColor=Color.FromArgb(162,121,80);serverValue.Font=new Font(Font,FontStyle.Bold);
        firstEvent.Minimum=1;firstEvent.Dock=DockStyle.Fill;firstEvent.ThousandsSeparator=true;
        note.Text="الضبط تلقائي. التعديل اليدوي يغيّر بداية الاستقبال فقط. الحركات المطبّقة سابقًا لا تتكرر. اختيار بداية لاحقة يتجاوز الحركات التي قبلها.";
        note.Dock=DockStyle.Fill;note.ForeColor=Color.DimGray;
        save.Text="حفظ البداية";save.AutoSize=true;save.DialogResult=DialogResult.OK;
        save.BackColor=Color.FromArgb(162,121,80);save.ForeColor=Color.White;save.FlatStyle=FlatStyle.Flat;
        cancel.Text="إغلاق";cancel.AutoSize=true;cancel.DialogResult=DialogResult.Cancel;
        buttons.Dock=DockStyle.Fill;buttons.Controls.Add(save);buttons.Controls.Add(cancel);
        layout.Controls.Add(serverLabel,0,0);layout.Controls.Add(serverValue,1,0);
        layout.Controls.Add(localLabel,0,1);layout.Controls.Add(localValue,1,1);
        layout.Controls.Add(startLabel,0,2);layout.Controls.Add(firstEvent,1,2);
        layout.Controls.Add(note,0,3);layout.SetColumnSpan(note,2);
        layout.Controls.Add(buttons,0,4);layout.SetColumnSpan(buttons,2);
        Controls.Add(layout);AcceptButton=save;CancelButton=cancel;
        ResumeLayout(false);
    }
}
