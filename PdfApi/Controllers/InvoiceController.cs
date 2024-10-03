using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Bugsnag;
using ChicksGold.Data.Common;
using ChicksGold.Data.Enums;
using ChicksGold.Server.Authentication;
using ChicksGold.Server.Lib.Exceptions;
using ChicksGold.Server.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace PdfApi.Controllers
{
	[Authorize]
	[ApiController]
	[Route("[controller]")]
	public class InvoiceController(IPdfService pdfService, IClient bugSnag, IOrderService orderService, UserResolver userResolver) : ControllerBase
	{
		[HttpGet("{id}/GeneratePdfReceipt")]
		[ProducesResponseType(typeof(FileStreamResult), 200)]
		public async Task<IActionResult> GeneratePdfReceipt([FromRoute] long id)
		{
			try
			{
				var isStaff = await userResolver.HasRole(nameof(AuthorizationPolicyType.ViewAdminPanel));
				var (pdf, websiteShortCode) = await pdfService.GeneratePdf(id, User.GetId(), isStaff);
				var pdfStream = new MemoryStream(pdf);
				var estTime = pdfService.ConvertToEasternTime(DateTime.UtcNow);
				return new FileStreamResult(pdfStream, "application/pdf")
				{
					FileDownloadName = $"{websiteShortCode}_order_{id}_{estTime:MM/dd/yyyy hh:mm tt}.pdf"
				};
			}
			catch (Exception ex)
			{
#if !DEBUG
				bugSnag.Notify(ex);
#endif
				throw new ChicksGoldException(ex.Message);
			}
		}

		[HttpGet("GeneratePdfForEmail/{encryptedData}")]
		[ProducesResponseType(typeof(FileStreamResult), 200)]
		[AllowAnonymous]
		public async Task<IActionResult> GeneratePdfReceiptEmail([FromRoute] string encryptedData)
		{
			try
			{
				var pattern = @"^\d+-\d+";
				var decryptedText = orderService.DecryptOrderAndUserId(encryptedData);
				var isMatch = Regex.IsMatch(decryptedText, pattern);

				if (!isMatch)
				{
					return Unauthorized();
				}
				var pdfInfo = decryptedText.Split("-");

				var orderId = Convert.ToInt64(pdfInfo[0]);
				var userId = Convert.ToInt64(pdfInfo[1]);
				var userApiKey = pdfInfo.Length > 2 && !string.IsNullOrEmpty(pdfInfo[2]) ? pdfInfo[2] : null;
				var (pdf, websiteShortCode) = await pdfService.GeneratePdf(orderId, userId, false, HttpContext.Connection.RemoteIpAddress.ToString(), userApiKey);
				var pdfStream = new MemoryStream(pdf);
				return new FileStreamResult(pdfStream, "application/pdf")
				{
					FileDownloadName = $"{websiteShortCode}_order_{orderId}_{DateTime.Now:MM/dd/yyyy hh:mm tt}.pdf"
				};
			}
			catch (Exception ex)
			{
#if !DEBUG
				bugSnag.Notify(ex);
#endif
				throw new ChicksGoldException(ex.Message);
			}
		}
	}
}
