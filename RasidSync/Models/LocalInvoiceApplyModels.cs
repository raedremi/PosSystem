namespace RasidSync.Models;

// نماذج داخلية صغيرة يحتاجها تطبيق الفاتورة، ولا تغيّر نماذج برنامج رصيد.
public sealed class InvoiceResyncRequest
{
    public InvoiceResyncHeader Invoice { get; set; } = new();
    public List<InvoiceResyncDetail> Details { get; set; } = [];
}

public sealed class InvoiceResyncHeader
{
    public int p_set_id { get; set; }
    public string? p_uuid { get; set; }
    public int p_inv_set_idinvo { get; set; }
    public int p_inv_type_pay { get; set; }
    public decimal p_inv_acc_item { get; set; }
    public decimal p_inv_acc_cash { get; set; }
    public decimal p_inv_acc_disc { get; set; }
    public decimal p_inv_acc_tax { get; set; }
    public int p_cus_id { get; set; }
    public decimal p_inv_befortax { get; set; }
    public decimal p_inv_tax { get; set; }
    public decimal p_inv_net { get; set; }
    public decimal p_inv_disc_total { get; set; }
    public decimal p_inv_paidup { get; set; }
    public DateTime p_inv_datetime { get; set; }
    public string? p_inv_users { get; set; }
    public int p_inv_b_id { get; set; }
    public int p_inv_savein_b_id { get; set; }
    public string? p_ck_num { get; set; }
    public string? p_inv_number_order { get; set; }
}

public sealed class InvoiceResyncDetail
{
    public int det_it_id { get; set; }
    public int det_set_idinv { get; set; }
    public decimal det_qty_in { get; set; }
    public decimal det_qty_out { get; set; }
    public decimal det_un_id { get; set; }
    public decimal det_un_unitequals { get; set; }
    public decimal det_store_id { get; set; }
    public DateTime det_datetime { get; set; }
    public decimal det_price_after_disc { get; set; }
}

public sealed class InvoiceJournalData
{
    public long InvoiceNumber { get; set; }
    public int InvoiceSetId { get; set; }
    public int InvoiceType { get; set; }
    public int PaymentTypeId { get; set; }
    public long ItemAccountId { get; set; }
    public long CashAccountId { get; set; }
    public long CustomerAccountId { get; set; }
    public long TaxAccountId { get; set; }
    public long DiscountAccountId { get; set; }
    public decimal BeforeTax { get; set; }
    public decimal TaxValue { get; set; }
    public decimal Net { get; set; }
    public decimal Discount { get; set; }
    public decimal PaidUp { get; set; }
    public DateTime InvoiceDateTime { get; set; }
    public long UserId { get; set; }
    public int BranchId { get; set; }
    public int SaveBranchId { get; set; }
}

public sealed class JournalEntry
{
    public long GlIdBranch { get; set; }
    public long AccountId { get; set; }
    public long AccountIdApp { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTime GlDateTime { get; set; }
    public DateTime GlDate { get; set; }
    public TimeSpan GlTime { get; set; }
    public long InvoiceNumber { get; set; }
    public long GeneralNumber { get; set; }
    public long UserId { get; set; }
    public int InvoiceSetId { get; set; }
    public int InvoiceType { get; set; }
    public int SaveBranchId { get; set; }
    public int BranchId { get; set; }
}

public sealed class LocalPayment
{
    public int pay_id { get; set; }
    public string? pay_name { get; set; }
    public decimal pay_percent { get; set; }
    public int pay_accountid { get; set; }
    public int pay_show { get; set; }
    public string? pay_type_name { get; set; }
    public int pay_acc_disc { get; set; }
}
