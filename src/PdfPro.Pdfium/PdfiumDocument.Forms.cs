using PDFiumCore;
using PdfPro.Core.Forms;

namespace PdfPro.Pdfium;

/// <summary>
/// The form-fill layer of a document: PDFium draws form fields (widgets) only through it (FPDF_FFLDraw), so every
/// document with a form gets one, made the first time a page is rendered. No callbacks are given (no JavaScript, no
/// focus, no timers): PDF_Pro fills forms itself and only needs the fields drawn.
/// </summary>
public sealed partial class PdfiumDocument
{
    private FPDF_FORMFILLINFO? _formInfo;
    private FpdfFormHandleT? _form;
    private bool _formChecked;

    /// <summary>What kind of form the document has.</summary>
    public FormType FormType
    {
        get
        {
            var type = fpdf_formfill.FPDF_GetFormType(Native);
            return Enum.IsDefined((FormType)type) ? (FormType)type : FormType.None;
        }
    }

    /// <summary>The form-fill environment, made on first use for a document with a form; null without one.</summary>
    private FpdfFormHandleT? FormHandle()
    {
        if (_formChecked)
        {
            return _form;
        }

        _formChecked = true;
        var native = Native;
        if (FormType == FormType.None)
        {
            return null;
        }

        var info = new FPDF_FORMFILLINFO();
        try
        {
            // The structure's native memory is not cleared on allocation: every callback is set to null explicitly.
            foreach (var property in typeof(FPDF_FORMFILLINFO).GetProperties())
            {
                if (property.CanWrite && property.Name is not nameof(FPDF_FORMFILLINFO.Version) and not "__Instance")
                {
                    property.SetValue(info, property.PropertyType.IsValueType ? Activator.CreateInstance(property.PropertyType) : null);
                }
            }

            info.Version = 1;
            var form = fpdf_formfill.FPDFDOC_InitFormFillEnvironment(native, info);
            if (form is null || form.__Instance == IntPtr.Zero)
            {
                info.Dispose();
                return null;
            }

            _formInfo = info;
            _form = form;
            return form;
        }
        catch
        {
            info.Dispose();
            throw;
        }
    }

    /// <summary>Leaves the form-fill environment (before the document closes).</summary>
    private void CloseForm()
    {
        if (_form is { } form)
        {
            fpdf_formfill.FPDFDOC_ExitFormFillEnvironment(form);
            _form = null;
        }

        _formInfo?.Dispose();
        _formInfo = null;
    }
}
