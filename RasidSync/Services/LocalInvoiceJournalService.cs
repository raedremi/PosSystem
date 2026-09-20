using RasidSync.Models;
using Dapper;
using System.Data;

namespace RasidSync.Services
{
    /// <summary>
    /// توليد وحفظ سند الفاتورة بنفس اتجاهات القيود المستخدمة في برنامج رصيد.
    /// السداد المتعدد غير مفعل هنا؛ نعالج طريقة الدفع الواحدة المختارة في Android.
    /// </summary>
    public static class LocalInvoiceJournalService
    {
        private static readonly int[] SupportedInvoiceTypes = { 102, 103, 202, 203 };

        public static async Task CreateAsync(
            IDbConnection conn,
            IDbTransaction tran,
            InvoiceJournalData invoice)
        {
            // بقية أنواع الحركات لا تولد سند فاتورة.
            if (!SupportedInvoiceTypes.Contains(invoice.InvoiceType))
                return;

            // إعادة إرسال الفاتورة لا يجب أن يولد سنداً ثانياً.
            int oldRows = await conn.ExecuteScalarAsync<int>(
                @"SELECT COUNT(*)
                  FROM tbl_gl
                  WHERE gl_inv_id = @InvoiceNumber
                    AND gl_inv_set_id = @InvoiceSetId
                    AND gl_set_idinvo = @InvoiceType
                    AND gl_savein_b_id = @SaveBranchId",
                invoice,
                tran);

            if (oldRows > 0)
                return;

            // رقم طريقة الدفع المرسل من Android يكفي لجلب نوعها وحسابها من السيرفر.
            var payment = await conn.QuerySingleOrDefaultAsync<LocalPayment>(
                @"SELECT pay_id, pay_name, pay_percent, pay_accountid,
                         pay_show, pay_type_name, pay_acc_disc
                  FROM tbl_payment
                  WHERE pay_id = @PaymentTypeId
                  LIMIT 1",
                new { invoice.PaymentTypeId },
                tran);

            if (payment == null)
                throw new InvalidOperationException(
                    $"طريقة الدفع رقم {invoice.PaymentTypeId} غير موجودة.");

            string paymentMethod =
                (payment.pay_type_name ?? string.Empty).Trim();

            if (paymentMethod != "Cash" &&
                paymentMethod != "Credit" &&
                paymentMethod != "Network" &&
                paymentMethod != "CC")
            {
                throw new InvalidOperationException(
                    $"نوع طريقة الدفع '{paymentMethod}' غير مدعوم في سند الفاتورة.");
            }

            var entries = Generate(invoice, paymentMethod, payment);

            ValidateEntries(entries);

            // برنامج رصيد يستخدم الرقم الحالي ثم يزيده.
            // LAST_INSERT_ID يجعل الحجز ذرياً حتى مع MyISAM وتزامن أكثر من فاتورة.
            await conn.ExecuteAsync(
                @"UPDATE tbl_number
                  SET num_gl_general = LAST_INSERT_ID(num_gl_general + 1)
                  WHERE num_id = 1",
                transaction: tran);

            long nextNumber = await conn.ExecuteScalarAsync<long>(
                "SELECT LAST_INSERT_ID()",
                transaction: tran);

            long generalNumber = nextNumber - 1;

            foreach (var entry in entries)
                entry.GeneralNumber = generalNumber;

            const string insertSql = @"
                INSERT INTO tbl_gl
                (
                    gl_id_branch, gl_ac_id, gl_ac_id_app,
                    gl_debit, gl_credit, gl_desc,
                    gl_datetime, gl_date, gl_time,
                    gl_inv_id, gl_num_general, gl_user_id,
                    gl_inv_set_id, gl_set_idinvo,
                    gl_savein_b_id, gl_b_id
                )
                VALUES
                (
                    @GlIdBranch, @AccountId, @AccountIdApp,
                    @Debit, @Credit, @Description,
                    @GlDateTime, @GlDate, @GlTime,
                    @InvoiceNumber, @GeneralNumber, @UserId,
                    @InvoiceSetId, @InvoiceType,
                    @SaveBranchId, @BranchId
                );";

            await conn.ExecuteAsync(insertSql, entries, tran);
        }

        private static List<JournalEntry> Generate(
            InvoiceJournalData invoice,
            string paymentMethod,
            LocalPayment payment)
        {
            var entries = new List<JournalEntry>();

            // مبيع ومرتجع شراء لهما الاتجاه نفسه، والشراء ومرتجع المبيع بالعكس.
            bool saleDirection =
                invoice.InvoiceType == 102 ||
                invoice.InvoiceType == 203;

            decimal baseValue =
                Round3(invoice.BeforeTax + invoice.Discount);

            AddPair(
                entries,
                invoice,
                invoice.ItemAccountId,
                invoice.CustomerAccountId,
                baseValue,
                saleDirection);

            // رصيد يضيف قيد الضريبة فقط عندما توجد قيمة ضريبة فعلية.
            if (invoice.TaxValue > 0)
            {
                AddPair(
                    entries,
                    invoice,
                    invoice.TaxAccountId,
                    invoice.CustomerAccountId,
                    Round3(invoice.TaxValue),
                    saleDirection);
            }

            // Credit آجل بالكامل: لا يوجد قيد صندوق أو بنك.
            if (paymentMethod == "Cash")
            {
                AddPaymentPair(
                    entries,
                    invoice,
                    invoice.CashAccountId,
                    Round3(invoice.Net),
                    saleDirection);
            }
            else if (paymentMethod == "Network")
            {
                AddPaymentPair(
                    entries,
                    invoice,
                    payment.pay_accountid,
                    Round3(invoice.Net),
                    saleDirection);

                // اقتطاع الشبكة موجود في رصيد لفاتورة المبيع فقط.
                if (invoice.InvoiceType == 102 && payment.pay_percent > 0)
                {
                    decimal networkDiscount =
                        Round3(invoice.Net * payment.pay_percent / 100m);

                    AddRawPair(
                        entries,
                        invoice,
                        payment.pay_acc_disc,
                        payment.pay_accountid,
                        networkDiscount,
                        firstAccountIsDebit: true);
                }
            }
            else if (paymentMethod == "CC" && invoice.PaidUp > 0)
            {
                // CC تعني آجل مع دفعة نقدية؛ المتبقي يبقى على حساب العميل/المورد.
                AddPaymentPair(
                    entries,
                    invoice,
                    invoice.CashAccountId,
                    Round3(invoice.PaidUp),
                    saleDirection);
            }

            // الخصم العام يعكس اتجاهه بين المبيع/مرتجع الشراء والشراء/مرتجع المبيع.
            if (invoice.Discount > 0)
            {
                AddRawPair(
                    entries,
                    invoice,
                    invoice.DiscountAccountId,
                    invoice.CustomerAccountId,
                    Round3(invoice.Discount),
                    firstAccountIsDebit: saleDirection);
            }

            return entries;
        }

        private static void AddPair(
            List<JournalEntry> entries,
            InvoiceJournalData invoice,
            long itemOrTaxAccount,
            long customerAccount,
            decimal value,
            bool saleDirection)
        {
            AddRawPair(
                entries,
                invoice,
                itemOrTaxAccount,
                customerAccount,
                value,
                firstAccountIsDebit: !saleDirection);
        }

        private static void AddPaymentPair(
            List<JournalEntry> entries,
            InvoiceJournalData invoice,
            long paymentAccount,
            decimal value,
            bool saleDirection)
        {
            if (value <= 0)
                return;

            AddRawPair(
                entries,
                invoice,
                paymentAccount,
                invoice.CustomerAccountId,
                value,
                firstAccountIsDebit: saleDirection);
        }

        private static void AddRawPair(
            List<JournalEntry> entries,
            InvoiceJournalData invoice,
            long firstAccount,
            long secondAccount,
            decimal value,
            bool firstAccountIsDebit)
        {
            if (value <= 0)
                return;

            entries.Add(CreateEntry(
                invoice,
                firstAccount,
                firstAccountIsDebit ? value : 0m,
                firstAccountIsDebit ? 0m : value));

            entries.Add(CreateEntry(
                invoice,
                secondAccount,
                firstAccountIsDebit ? 0m : value,
                firstAccountIsDebit ? value : 0m));
        }

        private static JournalEntry CreateEntry(
            InvoiceJournalData invoice,
            long accountId,
            decimal debit,
            decimal credit)
        {
            return new JournalEntry
            {
                GlIdBranch = invoice.SaveBranchId,
                AccountId = accountId,
                AccountIdApp = 0,
                Debit = Round3(debit),
                Credit = Round3(credit),
                Description = GetDescription(invoice.InvoiceType),
                GlDateTime = invoice.InvoiceDateTime,
                GlDate = invoice.InvoiceDateTime.Date,
                GlTime = invoice.InvoiceDateTime.TimeOfDay,
                InvoiceNumber = invoice.InvoiceNumber,
                UserId = invoice.UserId,
                InvoiceSetId = invoice.InvoiceSetId,
                InvoiceType = invoice.InvoiceType,
                SaveBranchId = invoice.SaveBranchId,
                BranchId = invoice.BranchId
            };
        }

        private static void ValidateEntries(List<JournalEntry> entries)
        {
            if (entries.Count == 0)
                throw new InvalidOperationException("لم يتم توليد أي سطر في سند الفاتورة.");

            foreach (var entry in entries)
            {
                if (entry.AccountId <= 0)
                    throw new InvalidOperationException(
                        "يوجد حساب غير صحيح أثناء توليد سند الفاتورة.");
            }

            decimal debit = Round3(entries.Sum(x => x.Debit));
            decimal credit = Round3(entries.Sum(x => x.Credit));

            if (debit != credit)
                throw new InvalidOperationException(
                    $"سند الفاتورة غير متوازن. المدين={debit:N3}، الدائن={credit:N3}.");
        }

        private static string GetDescription(int invoiceType)
        {
            return invoiceType switch
            {
                102 => "فاتورة مبيع",
                103 => "فاتورة شراء",
                202 => "مرتجع مبيع",
                203 => "مرتجع شراء",
                _ => "فاتورة"
            };
        }

        private static decimal Round3(decimal value) =>
            Math.Round(value, 3, MidpointRounding.AwayFromZero);
    }
}

