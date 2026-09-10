using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;

namespace Trykatch.Infrastructure.Modules.Documents;

public static class DocumentExporter
{
    public static byte[] CreateSpreadsheet(string sheetName, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        using MemoryStream stream = new();
        using (SpreadsheetDocument document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            WorkbookPart workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            WorksheetPart worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            SheetData data = new();
            foreach (IReadOnlyList<string> values in rows)
            {
                data.Append(new Row(values.Select(value => new Cell { DataType = CellValues.String, CellValue = new CellValue(value) })));
            }

            worksheetPart.Worksheet = new Worksheet(data);
            Sheets sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet { Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 1, Name = sheetName });
            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }

    public static byte[] CreatePdf(string title, IEnumerable<string> paragraphs)
    {
        Document document = new();
        Section section = document.AddSection();
        section.AddParagraph(title, "Heading1");
        foreach (string paragraph in paragraphs) section.AddParagraph(paragraph);
        PdfDocumentRenderer renderer = new() { Document = document };
        renderer.RenderDocument();
        using MemoryStream stream = new();
        renderer.PdfDocument.Save(stream, false);
        return stream.ToArray();
    }
}
