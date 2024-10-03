using System;
using System.Threading.Tasks;

namespace PdfApi
{
	public interface IPdfService
	{
		Task<(byte[] pdfStream, string websiteShortCode)> GeneratePdf(long orderId, long userId, bool isStaff = false, string requestIp = null, string pdfApiKey = null);
		DateTime ConvertToEasternTime(DateTime utcTime);
	}
}
