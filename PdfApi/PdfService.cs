using AutoMapper;
using ChicksGold.Data.Common;
using ChicksGold.Data.Extensions;
using ChicksGold.Data.Interfaces;
using ChicksGold.Data.Models.DatabaseModels;
using ChicksGold.Data.Models.DTOs.AccountCredential.Response;
using ChicksGold.Data.Models.DTOs.GiftCardKey.Response;
using ChicksGold.Data.Models.DTOs.Order.Response;
using ChicksGold.Data.Models.DTOs.OrderProduct.Response;
using ChicksGold.Data.Models.DTOs.PaymentMethodWebsite.Response;
using ChicksGold.Data.Models.DTOs.User.Response;
using ChicksGold.Server.Lib.Exceptions;
using ChicksGold.Server.Lib.Helpers;
using ChicksGold.Server.Services.Interfaces;
using ChicksGold.Server.ThirdPartyServices.AWS;
using ChicksGold.Server.ThirdPartyServices.BlueSnap;
using ChicksGold.Server.ThirdPartyServices.Checkout;
using ChicksGold.Server.ThirdPartyServices.NMI;
using ChicksGold.Server.ThirdPartyServices.PaysafeRest;
using ChicksGold.Server.ThirdPartyServices.Solidgate;
using HandlebarsDotNet;
using iText.Html2pdf;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using PdfApi.Formatters;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PdfApi
{
	public class PdfService : IPdfService
	{
		private readonly IOrderRepository _orderRepository;
		private readonly IPaysafeClient _paysafeClient;
		private readonly IBlueSnapCreditCardClient _blueSnapCreditCardClient;
		private readonly IBlueSnapHostedPageClient _blueSnapHostedPageClient;
		private readonly ICheckoutCreditCardClient _checkoutCreditCardClient;
		private readonly IPaymentMethodWebsiteRepository _paymentMethodWebsiteRepository;
		private readonly IMapper _mapper;
		private readonly IMemoryCache _cache;
		private readonly IUserLogRepository _userLogRepository;
		private readonly IWebsiteService _websiteService;
		private readonly IAccountCredentialRepository _accountCredentialRepository;
		private readonly IGiftCardKeyRepository _giftCardKeyRepository;
		private readonly ISolidgateCreditCardClient _solidgateCreditCardClient;
		private readonly INMICreditCardClient _nmiCreditCardClient;
		private readonly string _basePath;
		private readonly string _stylesPath;
		private readonly string _imagePath;
		private readonly string _chicksGoldPdfApiBaseUrl;
		private readonly IHandlebars _globalHandleBars;
		private static readonly string[] s_staticProductCategoryNames = ["Currency", "Skins", "Account", "Item", "Gift Cards"];
		private static readonly string[] s_staticGameShortNames = ["BALANCE", "SUBSCRIPTION"];
		private static readonly string[] s_visaCardTypes = ["vi", "ve", "vd", "visa"];
		private static readonly string[] s_masterCardTypes = ["mc", "mastercard"];
		private static readonly string[] s_americanExpressCardTypes = ["am", "amex", "american express"];
		private static readonly string[] s_dinersCardTypes = ["dc", "diners", "diners club"];
		private static readonly string[] s_discoverCardTypes = ["di", "discover"];
		private readonly string _pdfApiKey;
		private readonly IS3Client _s3Client;

		public PdfService(
			IOrderRepository orderRepository,
			IPaysafeClient paysafeClient,
			IBlueSnapCreditCardClient blueSnapCreditCardClient,
			IBlueSnapHostedPageClient blueSnapHostedPageClient,
			ICheckoutCreditCardClient checkoutCreditCardClient,
			IMapper mapper,
			IPaymentMethodWebsiteRepository paymentMethodWebsiteRepository,
			IConfiguration configuration,
			IMemoryCache cache,
			IUserLogRepository userLogRepository,
			IWebsiteService websiteService,
			IAccountCredentialRepository accountCredentialRepository,
			IGiftCardKeyRepository giftCardKeyRepository,
			ISolidgateCreditCardClient solidgateCreditCardClient,
			INMICreditCardClient nmiCreditCardClient,
			IS3Client s3Client)
		{
			_orderRepository = orderRepository;
			_paysafeClient = paysafeClient;
			_blueSnapCreditCardClient = blueSnapCreditCardClient;
			_blueSnapHostedPageClient = blueSnapHostedPageClient;
			_checkoutCreditCardClient = checkoutCreditCardClient;
			_mapper = mapper;
			_cache = cache;
			_paymentMethodWebsiteRepository = paymentMethodWebsiteRepository;
			_userLogRepository = userLogRepository;
			_websiteService = websiteService;
			_accountCredentialRepository = accountCredentialRepository;
			_giftCardKeyRepository = giftCardKeyRepository;
			_solidgateCreditCardClient = solidgateCreditCardClient;
			_nmiCreditCardClient = nmiCreditCardClient;
			_chicksGoldPdfApiBaseUrl = configuration["ChicksPdfApiBaseUrl"];
			_pdfApiKey = configuration.PdfApiKey();
			_s3Client = s3Client;

#if DEBUG
			_basePath = Environment.CurrentDirectory;
			_stylesPath = @"styles/";
			_imagePath = @"images/";
#else
			_basePath = Environment.CurrentDirectory;
			_stylesPath = _basePath + @"/styles/";
			_imagePath = _basePath + @"/images/";
#endif

			var formatter = new DateTimeFormatter("dd MMMM yyyy, hh:mm tt");
			_globalHandleBars = Handlebars.CreateSharedEnvironment();
			_globalHandleBars.Configuration.FormatterProviders.Add(formatter);

			_globalHandleBars.RegisterHelper("getSubtotal", (writer, context, parameters) =>
			{
				var subtotal = 0.0M;
				var orderTypeName = parameters[0].ToString();
				var products = (List<OrderProductPdfResponse>)context["Products"];

				foreach (var product in ((List<OrderProductPdfResponse>)context["Products"]).Where(product => product.TotalPrice.HasValue))
				{
					product.Price /= product.Quantity;
					if ((bool)product.IsSell)
					{
						subtotal -= product.TotalPrice.Value;
					}
					else
					{
						subtotal += product.TotalPrice.Value - (product.TotalFees ?? 0.0M);
					}
				}
				if (subtotal < 0)
				{
					subtotal = -subtotal;
				}

				if (orderTypeName == "withdraw" && products.Any())
				{
					subtotal = (decimal)(products[0]?.ConvertedPrice);
				}
				writer.Write(subtotal);
			});

			_globalHandleBars.RegisterHelper("getTotal", (writer, context, parameters) =>
			{
				var currencyRate = Convert.ToDecimal(parameters[0]);
				var currency = parameters[1].ToString();
				var convertedCurrencyTotal = decimal.Round(Convert.ToDecimal(parameters[2]), 2);
				var balanceAmount = decimal.Round(Convert.ToDecimal(parameters[3]), 2);
				var transactionType = parameters[4].ToString();
				var originalRate = Convert.ToDecimal(parameters[5]);
				var orderTypeName = parameters[6].ToString();
				var totalPrice = Convert.ToDecimal(parameters[7]);

				if (balanceAmount > 0)
				{
					if (currency != "USD")
					{
						balanceAmount *= originalRate;
						balanceAmount = decimal.Round(balanceAmount, 2);
					}
					convertedCurrencyTotal -= balanceAmount;
				}

				if (convertedCurrencyTotal == 0 && transactionType == "Swap")
				{
					foreach (var product in ((List<OrderProductPdfResponse>)context["Products"]).Where(product => product.TotalPrice.HasValue))
					{
						product.Price /= product.Quantity;
						if ((bool)product.IsSell)
						{
							convertedCurrencyTotal -= product.TotalPrice.Value;
						}
						else
						{
							convertedCurrencyTotal += product.TotalPrice.Value - (product.TotalFees ?? 0.0M);
						}
					}

					if (convertedCurrencyTotal < 0)
					{
						convertedCurrencyTotal = -convertedCurrencyTotal;
					}
				}

				if (orderTypeName == "withdraw")
				{
					balanceAmount = 0;
					if (currency != "USD")
					{
						convertedCurrencyTotal = totalPrice / originalRate;
					}
					else
					{
						convertedCurrencyTotal = totalPrice;
					}
				}

				writer.Write(Math.Abs(convertedCurrencyTotal));
			});

			_globalHandleBars.RegisterHelper("getInsuranceName", (writer, context, parameters) =>
			{
				var insuranceName = "";
				foreach (var product in ((List<OrderProductPdfResponse>)context["Products"]).Where(product => product.InsuranceId.HasValue))
				{
					if (insuranceName != "") insuranceName += ", ";
					insuranceName += product.Insurance.DisplayName;
				}
				writer.Write(insuranceName);
			});

			_globalHandleBars.RegisterHelper("getInsuranceFee", (writer, context, parameters) =>
			{
				var insuranceFee = 0.0M;
				foreach (var product in ((List<OrderProductPdfResponse>)context["Products"]).Where(product => product.InsuranceFee.HasValue))
				{
					insuranceFee += product.InsuranceFee.Value;
				}
				writer.Write(insuranceFee);
			});

			_globalHandleBars.RegisterHelper("getIconClass", (writer, context, parameters) =>
			{
				var productCategoryName = parameters[0].ToString();

				var className = productCategoryName switch
				{
					"Items" or "Service" => "different-size",
					"Accounts" => "account-img",
					_ => "icon-img"
				};

				writer.WriteSafeString(className);
			});

			_globalHandleBars.RegisterHelper("getIcon", (writer, context, parameters) =>
			{
				var imagePath = parameters[0]?.ToString();
				var name = parameters[1]?.ToString();
				var productName = parameters[2].ToString();
				var productCategoryName = parameters[3].ToString();
				var gameShortName = parameters[4].ToString();
				var orderStatus = parameters[5].ToString();

				if (s_staticProductCategoryNames.Any(x => productCategoryName.Contains(x)) || productName.Contains("Balance") || s_staticGameShortNames.Any(x => gameShortName.Contains(x)))
				{
					switch (productCategoryName)
					{
						case "Currency":
							if (!string.IsNullOrEmpty(imagePath))
							{
								Task.Run(async () =>
								{
									var imgBytes = await _s3Client.GetS3BinaryFile(imagePath, "chicks-products", true);
									if (imgBytes != null && imgBytes.Content.Length > 0)
									{
										var base64String = Convert.ToBase64String(imgBytes.Content);
										writer.WriteSafeString($"data:{imgBytes.ContentType};base64,{base64String}");
									}
									else
									{
										writer.WriteSafeString("__images__/no-image-icon.png");
									}
								}).Wait();
							}
							else if (gameShortName.Contains("WOWL"))
							{
								writer.WriteSafeString("__images__/wow-retail-currency-icon.png");
							}
							else if (gameShortName.Contains("WOWTBC"))
							{
								writer.WriteSafeString("__images__/wow-classic-tbc-currency-icon.png");
							}
							else if (gameShortName.Contains("WOWSOM"))
							{
								writer.WriteSafeString("__images__/wow-classic-som-currency-icon.png");
							}
							else if (gameShortName.Contains("WOW") || gameShortName.Contains("WOWWOTLK"))
							{
								writer.WriteSafeString("__images__/wow-classic-currency-icon.png");
							}
							else
							{
								writer.WriteSafeString("__images__/no-image-icon.png");
							}
							break;
						default:
							if (productName == "Balance")
							{
								writer.WriteSafeString("__images__/balance.png");
							}
							else if (gameShortName.Contains("BALANCE"))
							{
								if (orderStatus.Contains("refunded"))
								{
									writer.WriteSafeString("__images__/balance-withdrawal.png");
								}
								else
								{
									writer.WriteSafeString($"__images__/balance-{productName.ToLower()}.png");
								}
							}
							else if (gameShortName.Contains("SUBSCRIPTION"))
							{
								writer.WriteSafeString("__images__/vip_chicks.png");
							}
							else if (!string.IsNullOrEmpty(imagePath))
							{
								Task.Run(async () =>
								{
									var imgBytes = await _s3Client.GetS3BinaryFile(imagePath, "chicks-products", true);
									if (imgBytes != null && imgBytes.Content.Length > 0)
									{
										var base64String = Convert.ToBase64String(imgBytes.Content);
										writer.WriteSafeString($"data:{imgBytes.ContentType};base64,{base64String}");
									}
									else
									{
										writer.WriteSafeString("__images__/no-image-icon.png");
									}
								}).Wait();
							}
							else
							{
								writer.WriteSafeString("__images__/no-image-icon.png");
							}
							break;
					}
				}
				else
				{
					var words = name.Split("|");
					var game = words[0].Replace(" ", "").ToLower();
					var skillNamePrev = Regex.Replace(words[1], "[^a-zA-Z]", " ").ToLower();
					var skillName = skillNamePrev.Replace(" ", "");

					if (skillName.Contains("quest"))
					{
						writer.WriteSafeString("__images__/osrs-questing-icon.png");
					}
					else if (skillName.Contains("diary"))
					{
						writer.WriteSafeString("__images__/diaries-osrs-product-icon.png");
					}
					else if (skillName.Contains("pvm"))
					{
						writer.WriteSafeString("__images__/pvm-osrs-product-icon.png");
					}
					else if (skillName.Contains("minigames"))
					{
						writer.WriteSafeString("__images__/minigame-product-icon.png");
					}
					else if (game.Contains("osrs") || game.Contains("rs3"))
					{
						writer.WriteSafeString($"__images__/skills/{game}/{skillName}.png");
					}
					else if (game.Contains("nw"))
					{
						writer.WriteSafeString("__images__/nw-small.png");
					}
					else if (game.Contains("lol"))
					{
						writer.WriteSafeString("__images__/lol-small.png");
					}
				}
			});

			_globalHandleBars.RegisterHelper("orderProductStatus", (writer, context, parameters) =>
			{
				var status = parameters[0].ToString();
				if (status.Contains("refunded"))
				{
					writer.WriteSafeString("<span class='text-red'>Refunded</span>");
				}
				else if (status.Contains("partially-refunded"))
				{
					writer.WriteSafeString("<span class='text-red'>Partially Refunded</span>");
				}
				else if (status.Contains("refund-requested"))
				{
					writer.WriteSafeString("<span class='text-yellow'>Refund Pending</span>");
				}
				else if (status.Contains("created"))
				{
					writer.WriteSafeString("<span class='text-yellow'>Created</span>");
				}
				else if (status.Contains("rejected"))
				{
					writer.WriteSafeString("<span class='text-red'>Rejected</span>");
				}
				else if ((int)context["FulfilledAmount"] >= (int)context["Quantity"])
				{
					writer.WriteSafeString("<span class='text-green'>Complete</span>");
				}
				else
				{
					writer.WriteSafeString("<span class='text-yellow'>Pending</span>");
				}
			});

			_globalHandleBars.RegisterHelper("price", (writer, context, parameters) =>
			{
				var currencyRate = Convert.ToDecimal(parameters[0]);
				var currency = parameters[1].ToString();
				var price = decimal.Round(Convert.ToDecimal(parameters[2]), 2);
				var conversion = parameters[3].ToString();

				string orderTypeName = null;
				decimal? feeTotal = null;
				decimal? originalRateUsed = null;

				if (parameters.Length > 4)
				{
					orderTypeName = parameters[4].ToString();
				}

				if (parameters.Length > 5)
				{
					feeTotal = Convert.ToDecimal(parameters[5]);
				}

				if (parameters.Length > 6)
				{
					originalRateUsed = Convert.ToDecimal(parameters[6]);
				}

				if (conversion == "convert" && currency != "USD" && orderTypeName != "withdraw")
				{
					price *= currencyRate;
				}

				if (orderTypeName == "withdraw" && feeTotal is not null && originalRateUsed is not null)
				{
					price = (decimal)(feeTotal / originalRateUsed);
				}
				var newPrice = string.Format("{0:0.00}", price).Replace(',', '.');
				writer.WriteSafeString($"{CurrencyCodeMapper.GetSymbol(currency)}{newPrice}");
			});

			_globalHandleBars.RegisterHelper("cryptoPrice", (writer, context, parameters) =>
			{
				var value = Convert.ToDecimal(parameters[0]);
				var symbol = parameters[1].ToString();
				var type = parameters[2].ToString();
				var isStable = Convert.ToBoolean(parameters[3]);
				var isRate = Convert.ToBoolean(parameters[4]);

				if (isRate)
				{
					value = type == "C" ? value : 1 / value;
				}

				var newPrice = string.Format(isStable ? "{0:0.00}" : "{0:0.000000}", value).Replace(',', '.');
				writer.WriteSafeString(type == "F" ? $"{symbol}{newPrice}" : $"{newPrice} {symbol}");
			});

			_globalHandleBars.RegisterHelper("ExchangePrice", (writer, context, parameters) =>
			{
				var value = Convert.ToDecimal(parameters[0]);
				var symbol = parameters[1].ToString();
				var isStable = Convert.ToBoolean(parameters[2]);
				var swapRate = Convert.ToBoolean(parameters[3]);

				value = swapRate ? 1 / value : value;

				var newPrice = string.Format(isStable ? "{0:0.00}" : "{0:0.000000}", value).Replace(',', '.');
				writer.WriteSafeString($"{newPrice} {symbol}");
			});

			_globalHandleBars.RegisterHelper("isExchange", (writer, options, context, parameters) =>
			{
				var baseCurrencyType = parameters[0].ToString();
				var targetCurrencyType = parameters[1].ToString();
				if (baseCurrencyType == targetCurrencyType)
				{
					options.Template(writer, context);
				}
				else
				{
					options.Inverse(writer, context);
				}
			});

			_globalHandleBars.RegisterHelper("helloMessage", (writer, context, parameters) =>
			{
				var status = parameters[0].ToString();
				var userFirstName = parameters[1].ToString();

				if (status.Contains("rejected"))
				{
					writer.WriteSafeString($"Hello, {userFirstName}.");
				}
				else
				{
					writer.WriteSafeString($"Thanks for ordering, {userFirstName}.");
				}
			});

			_globalHandleBars.RegisterHelper("mainMessage", (writer, context, parameters) =>
			{
				var shortCode = parameters[0].ToString();
				var status = parameters[1].ToString();
				var paymentMethodReference = parameters[2].ToString();
				var currencyUsed = parameters[3].ToString();
				var convertedCurrencyTotal = Convert.ToDecimal(parameters[4]);
				var cardLast4 = parameters[5]?.ToString();
				var baseSymbol = parameters[6]?.ToString();
				var amountBaseCrypto = decimal.Round(Convert.ToDecimal(parameters[7]?.ToString()), 2);
				var companyName = _websiteService.GetCompanyName(shortCode);
				var orderTypeName = parameters[8]?.ToString();
				var totalPrice = Convert.ToDecimal(parameters[9]);
				var originalRate = Convert.ToDecimal(parameters[10]);

				if (orderTypeName == "withdraw")
				{
					if (currencyUsed != "USD")
					{
						convertedCurrencyTotal = totalPrice / originalRate;
					}
				}

				if (status.Contains("rejected"))
				{
					if (!string.IsNullOrEmpty(cardLast4))
					{
						writer.Write($"Your order attempt at {companyName} failed to process. A temporary hold of {(shortCode == "CX" ? baseSymbol : CurrencyCodeMapper.GetSymbol(currencyUsed))}" +
							$"{string.Format("{0:0.00}", convertedCurrencyTotal > 0 ? convertedCurrencyTotal : amountBaseCrypto).Replace(',', '.')} was placed on your payment method. This is not a charge and will " +
							$"be removed. It should disappear from your bank statement shortly.");
					}
					else
					{
						writer.Write($"Your order attempt at {companyName} failed to process. Any temporary hold or authorization in the amount of {(shortCode == "CX" ? baseSymbol : CurrencyCodeMapper.GetSymbol(currencyUsed))}" +
							$"{string.Format("{0:0.00}", convertedCurrencyTotal > 0 ? convertedCurrencyTotal : amountBaseCrypto).Replace(',', '.')} on your payment method should disappear from your statement shortly.");
					}
				}
				else
				{
					if (!string.IsNullOrEmpty(cardLast4))
					{
						writer.Write($"Here's your receipt for {companyName} The total amount processed is {(shortCode == "CX" ? baseSymbol : CurrencyCodeMapper.GetSymbol(currencyUsed))}" +
								$"{string.Format("{0:0.00}", convertedCurrencyTotal > 0 ? convertedCurrencyTotal : amountBaseCrypto).Replace(',', '.')} and has been charged to payment method {(!string.IsNullOrEmpty(cardLast4) ? $"**** {cardLast4}." : ".")}");
					}
					else
					{
						writer.Write($"Here's your receipt for {companyName} The total amount processed is {(shortCode == "CX" ? baseSymbol : CurrencyCodeMapper.GetSymbol(currencyUsed))}" +
							$"{string.Format("{0:0.00}", convertedCurrencyTotal > 0 || shortCode == "CG" ? convertedCurrencyTotal < 0 ? convertedCurrencyTotal * -1 : convertedCurrencyTotal : amountBaseCrypto).Replace(',', '.')}.");
					}
				}
			});

			_globalHandleBars.RegisterHelper("getCardImageType", (writer, context, parameters) =>
			{
				var cardLast4 = parameters[0].ToString();
				var cardType = parameters[1].ToString();
				var pixels = 32;
				var icon = "__images__/payment-methods/generic.png";
				cardType = cardType?.ToLower();

				if (s_visaCardTypes.Equals(cardType))
				{
					pixels = 42;
					icon = "__images__/payment-methods/visa.png";
				}
				else if (s_masterCardTypes.Equals(cardType))
				{
					icon = "__images__/payment-methods/mastercard.png";
				}
				else if (s_americanExpressCardTypes.Equals(cardType))
				{
					pixels = 42;
					icon = "__images__/payment-methods/amex.png";
				}
				else if (s_dinersCardTypes.Equals(cardType))
				{
					icon = "__images__/payment-methods/diners.png";
				}
				else if (s_discoverCardTypes.Equals(cardType))
				{
					icon = "__images__/payment-methods/discover.png";
				};

				writer.WriteSafeString($"<img class='mb-1 me-2' src='{icon}' style='width: {pixels}px;' alt='credit card icon' loading='lazy'>");
			});

			_globalHandleBars.RegisterHelper("getPaymentAddress", (writer, context, parameters) =>
			{
				var paymentAddress = parameters[0]?.ToString();
				var paymentMethodReference = parameters[1].ToString();
				var paymentMethodStatus = parameters[2].ToString();

				if (!string.IsNullOrEmpty(paymentAddress) && paymentMethodReference != "crypto" || paymentMethodReference == "crypto" && paymentMethodStatus.Contains("complete"))
				{
					writer.WriteSafeString($"<span class='font-14 ms-2'>Payment Address: {paymentAddress}</span>");
				}
			});

			_globalHandleBars.RegisterHelper("greaterThan", (writer, options, context, parameters) =>
			{
				var type = parameters[0].ToString();
				var value = Convert.ToDecimal(parameters[1]);
				var customValue = 0.0M;

				if (type == "InsuranceFee")
				{
					foreach (var product in ((List<OrderProductPdfResponse>)context["Products"]).Where(product => product.InsuranceFee.HasValue))
					{
						customValue += product.InsuranceFee.Value;
					}
				}
				else
				{
					customValue = Convert.ToDecimal(type);
				}

				if (customValue > value)
					options.Template(writer, context);
				else
					options.Inverse(writer, context);
			});

			_globalHandleBars.RegisterHelper("equals", (writer, options, context, parameters) =>
			{
				var property = parameters[0].ToString();
				var value = parameters[1].ToString();

				if (property == value)
					options.Template(writer, context);
				else
					options.Inverse(writer, context);
			});

			_globalHandleBars.RegisterHelper("getCompanyName", (writer, context, parameters) =>
			{
				var shortCode = parameters[0].ToString();
				var companyName = _websiteService.GetCompanyName(shortCode);

				writer.WriteSafeString(companyName);
			});

			_globalHandleBars.RegisterHelper("getCompanyAddress", (writer, context, parameters) =>
			{
				var shortCode = parameters[0].ToString();

				switch (shortCode)
				{
					case "CG":
						writer.WriteSafeString("1 King Street W, Suite 4800" +
							"<br/>" +
							"Toronto, ON, Canada, M5H 1A1" +
							"<br/>" +
							"789572336");
						break;
					case "CX":
						writer.WriteSafeString("1 Yonge St 1801" +
							"<br/>" +
							"Toronto, ON, Canada, M5E 1W7" +
							"<br/>" +
							"787668540");
						break;
					case "DS":
						writer.WriteSafeString("1 King Street W, Suite 4800" +
							"<br/>" +
							"Toronto, ON, Canada, M5H 1A1");
						break;
					default:
						writer.WriteSafeString("1 King Street W, Suite 4800" +
							"<br/>" +
							"Toronto, ON, Canada, M5H 1A1" +
							"<br/>" +
							"789572336");
						break;
				}
			});

			_globalHandleBars.RegisterHelper("getCompanyEmailAddress", (writer, context, parameters) =>
			{
				var shortCode = parameters[0].ToString();
				var supportEmail = _websiteService.GetSupportEmail(shortCode);

				writer.WriteSafeString(supportEmail);
			});

			_globalHandleBars.RegisterHelper("getCompanyWebsiteAddress", (writer, context, parameters) =>
			{
				var shortCode = parameters[0].ToString();
				var websiteAddress = _websiteService.GetUrl(shortCode);
				var logo = _websiteService.GetLogo(shortCode);

				writer.WriteSafeString($"<img class='company-logo' src='__images__/company-logos/{logo}.png' alt='company logo' loading='lazy'/><br>" +
					$"<div>{websiteAddress}</div>");
			});

			_globalHandleBars.RegisterHelper("getUserAddressAndBillingInfo", (writer, context, parameters) =>
			{
				var address = parameters[0]?.ToString();
				var city = parameters[1]?.ToString();
				var state = parameters[2]?.ToString();
				var country = parameters[3]?.ToString();
				var zip = parameters[4]?.ToString();
				var billingList = new List<string>();
				if (!string.IsNullOrEmpty(city)) billingList.Add(city);
				if (!string.IsNullOrEmpty(state)) billingList.Add(state);
				if (!string.IsNullOrEmpty(country)) billingList.Add(country);
				if (!string.IsNullOrEmpty(zip)) billingList.Add(zip);
				var unifiedBilling = string.Join(", ", billingList);

				if (!string.IsNullOrEmpty(address))
				{
					writer.WriteSafeString($"{address}<br>{unifiedBilling}");
				}
				else
				{
					writer.WriteSafeString(unifiedBilling);
				}
			});

			_globalHandleBars.RegisterHelper("getColoredLine", (writer, context, parameters) =>
			{
				var shortCode = parameters[0].ToString();
				var coloredLine = "";

				coloredLine = shortCode switch
				{
					"CG" => "green-line",
					"CX" => "purple-line",
					"DS" => "green-line",
					_ => "green-line",
				};

				writer.WriteSafeString($"<div class='{coloredLine}'></div>");
			});

			_globalHandleBars.RegisterHelper("sanitizeHtml", (writer, context, parameters) =>
			{
				var value = parameters[0]?.ToString() ?? "";
				value = Regex.Replace(value, "<.*?>", string.Empty).Trim();
				writer.WriteSafeString(value);
			});

			_globalHandleBars.RegisterHelper("cgIf", (writer, options, context, parameters) =>
			{
				var accountCredentials = parameters[0] as List<AccountCredentialPdfResponse>;
				var giftCardKeys = parameters[1] as List<GiftCardKeyPdfResponse>;
				var anyExists = accountCredentials.Any() || giftCardKeys.Any();

				if (anyExists)
				{
					options.Template(writer, context);
				}
				else
				{
					options.Inverse(writer, context);
				}
			});

		}
		public bool HasProductSell(List<OrderProductPdfResponse> products)
		{
			foreach (var product in products)
			{
				if ((bool)product.IsSell)
				{
					return true;
				}
			}
			return false;
		}

		private async Task UserSpamCheck(string ip, int requests = 5, int timer = 2, string timerType = "hours", OrderCustomerInfoBasicResponse user = null)
		{
#if !DEBUG && !DEVELOPMENT && !STAGING

			if (GlobalFunctions.CheckIfIpFromChicksXStore(ip) || (user is not null && (user.IsEmployee || user.IsSuperAdmin)))
			{
				return;
			}

			var timeCap = timerType switch
			{
				"hours" => DateTime.UtcNow.Subtract(new TimeSpan(timer, 0, 0)),
				"minutes" => DateTime.UtcNow.Subtract(new TimeSpan(0, timer, 0)),
				_ => DateTime.UtcNow.Subtract(new TimeSpan(timer, 0, 0))
			};

			if (await _userLogRepository.SearchByIpOrFingerprintsAndTimeAsync(ip, null, timeCap) >= requests)
			{
				throw new UserSpamException("You have requested PDF download too many times recently.");
			}
#endif
		}

		private async Task<long> LogUserRequest(long userId, string ip)
		{

			var userLog = new UserLog()
			{
				UserId = userId,
				Ip = ip

			};

			return await _userLogRepository.AddAsync(userLog);
		}

		public async Task<(byte[] pdfStream, string websiteShortCode)> GeneratePdf(long orderId, long userId, bool isStaff = false, string requestIp = null, string pdfApiKey = null)
		{
			var order = await _orderRepository.GetByIdAsync<OrderPdfResponse>(orderId) ?? throw new EntityNotFoundException($"Order not found with ID: {orderId}");
			GlobalFunctions.CheckIfAuthorizedToProceed(order.UserId, userId, false, isStaff);
			var paymentMethodWebsite = await _paymentMethodWebsiteRepository.GetByIdsAsync<CustomerOrderPaymentMethodWebsiteResponse>(order.PaymentMethodId.Value, order.Website.Id);

			if (requestIp != null && (string.IsNullOrEmpty(pdfApiKey) || (!string.IsNullOrEmpty(pdfApiKey) && pdfApiKey != _pdfApiKey)))
			{
				await UserSpamCheck(requestIp, 5, 2, "hours", order.User);
				await LogUserRequest(userId, requestIp);
			}

			if (paymentMethodWebsite != null)
			{
				order.PaymentMethodWebsite = paymentMethodWebsite;
			}

			order.SellProducts = [];
			order.BuyProducts = [];

			foreach (var product in order.Products)
			{
				if ((bool)product.IsSell)
				{
					order.SellProducts.Add(product);
				}
				else
				{
					order.BuyProducts.Add(product);
				}
			}

			var orderAccountCredentials = await _accountCredentialRepository.GetCredentialsByOrderId(orderId);
			var accountCredentials = new List<AccountCredentialPdfResponse>();

			foreach (var orderAccountCredential in orderAccountCredentials)
			{
				if (orderAccountCredential.Status.ConstantKey == "Sold")
				{
					var product = order.Products.Where(x => x.ProductId == orderAccountCredential.ProductId).FirstOrDefault();
					var credential = new AccountCredentialPdfResponse
					{
						Username = orderAccountCredential.Username,
						Password = orderAccountCredential.Password,
						InternalId = orderAccountCredential.InternalId,
						Product = product,
						FulfilledAmount = 1,
						Quantity = 1,
						ConvertedPrice = product.ConvertedPrice / product.Quantity
					};
					accountCredentials.Add(credential);
				}
			}
			order.AccountCredentials = accountCredentials;

			var orderGiftcardKeys = await _giftCardKeyRepository.GetGiftCardKeysByOrderId(orderId);
			var giftCardKeys = new List<GiftCardKeyPdfResponse>();

			foreach (var orderGiftcardKey in orderGiftcardKeys)
			{
				if (orderGiftcardKey.Status.ConstantKey == "Sold")
				{
					var product = order.Products.Where(x => x.ProductId == orderGiftcardKey.ProductId).FirstOrDefault();
					var giftCardKey = new GiftCardKeyPdfResponse
					{
						Key = orderGiftcardKey.Key,
						InternalId = orderGiftcardKey.InternalId,
						CharacterName = product.Character,
						Product = product,
						FulfilledAmount = 1,
						Quantity = 1,
						ConvertedPrice = product.ConvertedPrice / product.Quantity
					};
					giftCardKeys.Add(giftCardKey);
				}
			}

			order.GiftCardKeys = giftCardKeys;
			order.CreatedDate = ConvertToEasternTime((DateTime)order.CreatedDate);
			order.IsSell = order.SellProducts.Count > 0;
			order.IsBuy = order.BuyProducts.Count > 0;
			order.IsCustom = (bool)order.IsBuy && !(bool)order.IsSell;
			if (Constants.GetAutomaticPaymentMethods().Contains(order.PaymentMethod.Reference))
			{
				if (order.PaysafeAuthorizationId != null || order.PaysafeMerchantRefNum != null)
				{
					var paysafeDetails = await _paysafeClient.RetrievePaysafeAuthorization(_mapper.Map<Order>(order), order.Website.ShortCode);
					order.CardLast4 = paysafeDetails?.Card?.LastDigits;
					order.CardType = paysafeDetails?.Card?.Type;
				}
				else if (order.BlueSnapTransactionId != null || order.PaymentMethod.Reference == "bluesnap")
				{
					var blueSnapDetails = await _blueSnapCreditCardClient.RetrieveTransactionDetails(order.BlueSnapTransactionId, order.BlueSnapUsTransaction.HasValue, order.Website.ShortCode);
					order.CardLast4 = blueSnapDetails?.CreditCard?.CardLastFourDigits;
					order.CardType = blueSnapDetails?.CreditCard?.CardType;
				}
				else if (order.BluesnapCheckoutTransactionId != null)
				{
					var blueSnapCheckoutDetails = await _blueSnapHostedPageClient.RetrieveTransactionDetails(_mapper.Map<OrderEmployeeResponse>(order));
					order.CardLast4 = blueSnapCheckoutDetails?.CreditCard?.CardLastFourDigits;
					order.CardType = blueSnapCheckoutDetails?.CreditCard?.CardType;
				}
				else if (order.CheckoutTransactionId != null)
				{
					var checkoutDetails = await _checkoutCreditCardClient.RetrieveTransactionDetails(order.CheckoutTransactionId);
					order.CardLast4 = checkoutDetails.Source.Last4;
					order.CardType = checkoutDetails.Source.Scheme;
				}
				else if (order.SolidgateTransactionId != null)
				{
					var solidgateDetails = await _solidgateCreditCardClient.RetrieveTransactionDetails(order.Id.ToString());
					order.CardLast4 = solidgateDetails.Transactions[order.SolidgateTransactionId].Card.Number[^4..];
					order.CardType = solidgateDetails.Transactions[order.SolidgateTransactionId].Card.Brand;
				}
				else if (order.NMITransactionId != null)
				{
					var nmiDetails = await _nmiCreditCardClient.RetrieveTransactionDetails(order.NMITransactionId);
					var ccNumber = nmiDetails?.Transaction?[0].CcNumber;
					order.CardLast4 = !string.IsNullOrEmpty(ccNumber) ? ccNumber[^4..] : "";
					order.CardType = nmiDetails?.Transaction?[0].CcType;
				}
			}

			var htmlPath = System.IO.Path.Combine(_basePath, @"Templates/Receipt.html");
			var htmlSource = await File.ReadAllTextAsync(htmlPath);

			var partialPath = System.IO.Path.Combine(_basePath, @"Templates/OrderItem.html");
			var partialSource = await File.ReadAllTextAsync(partialPath);

			var stockPartialPath = System.IO.Path.Combine(_basePath, @"Templates/StockItem.html");
			var stockPartialSource = await File.ReadAllTextAsync(stockPartialPath);

			_globalHandleBars.RegisterTemplate("orderItem", partialSource);
			_globalHandleBars.RegisterTemplate("stockItem", stockPartialSource);

			var compiledHtml = RenderTemplate("PdfTemplate", htmlSource, order);

			compiledHtml = compiledHtml.Replace("__styles__/", System.IO.Path.Combine(_basePath, _stylesPath));
			compiledHtml = compiledHtml.Replace("__images__/", System.IO.Path.Combine(_basePath, _imagePath));
			using var ms = new MemoryStream();
			var writer = new PdfWriter(ms);
			var pdfDoc = new PdfDocument(writer);
			var converterProperties = new ConverterProperties();
			pdfDoc.SetDefaultPageSize(new PageSize(702, 1080));
			HtmlConverter.ConvertToPdf(compiledHtml, pdfDoc, converterProperties);
			pdfDoc.Close();

			var pdfResult = new MemoryStream(ms.ToArray());
			var outputStream = new MemoryStream();
			var ws = new PdfWriter(outputStream);
			var pdf = new PdfDocument(new PdfReader(pdfResult), ws);
			var doc = new Document(pdf);
			var totalPages = pdf.GetNumberOfPages();
			var pageNumberFormat = "Page {0} of {1}";
			for (var pageNumber = 1; pageNumber <= totalPages; pageNumber++)
			{
				var page = pdf.GetPage(pageNumber);
				var pageNumberText = string.Format(pageNumberFormat, pageNumber, totalPages);
				var paragraph = new Paragraph(pageNumberText);
				doc.ShowTextAligned(paragraph,
						650, 10, pageNumber, TextAlignment.RIGHT, VerticalAlignment.BOTTOM, 0);
			}
			doc.Close();

			return (outputStream.ToArray(), order.Website.ShortCode.ToLower());
		}

		public static class CurrencyCodeMapper
		{
			private static readonly Dictionary<string, string> s_symbolsByCode = new()
			{
				{ "AFN", "AF" },
				{ "ALL", "L" },
				{ "AMD", "Դ" },
				{ "AOA", "KZ" },
				{ "ARS", "$" },
				{ "AUD", "AU$" },
				{ "AWG", "ƒ" },
				{ "AZN", "ман" },
				{ "BAM", "КМ" },
				{ "BBD", "$" },
				{ "BDT", "৳" },
				{ "BGN", "лв" },
				{ "BIF", "₣" },
				{ "BMD", "$" },
				{ "BND", "$" },
				{ "BOB", "BS." },
				{ "BRL", "R$" },
				{ "BSD", "$" },
				{ "BWP", "P" },
				{ "BYN", "BR" },
				{ "BZD", "$" },
				{ "CAD", "CA$" },
				{ "CDF", "₣" },
				{ "CHF", "FR" },
				{ "CLP", "$" },
				{ "CNY", "¥" },
				{ "COP", "$" },
				{ "CRC", "₡" },
				{ "CUP", "$" },
				{ "CVE", "$" },
				{ "CZK", "KČ" },
				{ "DJF", "₣" },
				{ "DKK", "KR" },
				{ "DOP", "$" },
				{ "EGP", "£" },
				{ "EUR", "€" },
				{ "FJD", "$" },
				{ "FKP", "£" },
				{ "GBP", "£" },
				{ "GEL", "ლ" },
				{ "GHS", "₵" },
				{ "GIP", "£" },
				{ "GMD", "D" },
				{ "GNF", "₣" },
				{ "GTQ", "Q" },
				{ "GYD", "$" },
				{ "HKD", "$" },
				{ "HNL", "L" },
				{ "HRK", "KN" },
				{ "HTG", "G" },
				{ "HUF", "FT" },
				{ "IDR", "RP" },
				{ "ILS", "₪" },
				{ "INR", "₹" },
				{ "ISK", "KR" },
				{ "JMD", "$" },
				{ "JPY", "¥" },
				{ "KHR", "៛" },
				{ "KPW", "₩" },
				{ "KRW", "₩" },
				{ "KYD", "$" },
				{ "KZT", "〒" },
				{ "LAK", "₭" },
				{ "LKR", "RS" },
				{ "LRD", "$" },
				{ "LSL", "L" },
				{ "MDL", "L" },
				{ "MKD", "ден" },
				{ "MMK", "K" },
				{ "MNT", "₮" },
				{ "MOP", "P" },
				{ "MRU", "UM" },
				{ "MUR", "₨" },
				{ "MVR", "ރ." },
				{ "MWK", "MK" },
				{ "MXN", "$" },
				{ "MYR", "RM" },
				{ "MZN", "MTN" },
				{ "NAD", "$" },
				{ "NGN", "₦" },
				{ "NIO", "C$" },
				{ "NOK", "KR" },
				{ "NPR", "₨" },
				{ "NZD", "NZ$" },
				{ "PAB", "B/." },
				{ "PEN", "S/." },
				{ "PGK", "K" },
				{ "PHP", "₱" },
				{ "PKR", "₨" },
				{ "PLN", "zł" },
				{ "PYG", "₲" },
				{ "RON", "L" },
				{ "RSD", "DIN" },
				{ "RUB", "P." },
				{ "RWF", "₣" },
				{ "SBD", "$" },
				{ "SCR", "₨" },
				{ "SDG", "£" },
				{ "SEK", "KR" },
				{ "SGD", "$" },
				{ "SHP", "£" },
				{ "SLL", "LE" },
				{ "SOS", "SH" },
				{ "SRD", "$" },
				{ "STN", "DB" },
				{ "SZL", "L" },
				{ "THB", "฿" },
				{ "TJS", "ЅМ" },
				{ "TMT", "M" },
				{ "TOP", "T$" },
				{ "TRY", "₤" },
				{ "TTD", "$" },
				{ "TWD", "$" },
				{ "TZS", "SH" },
				{ "UAH", "₴" },
				{ "UGX", "SH" },
				{ "USD", "$" },
				{ "UYU", "$" },
				{ "VND", "Đ" },
				{ "VUV", "VT" },
				{ "WST", "T" },
				{ "XAF", "₣" },
				{ "XCD", "$" },
				{ "XPF", "₣" },
				{ "ZAR", "R" },
				{ "ZMW", "ZK" },
				{ "ZWL", "$" }
			};

			public static string GetSymbol(string code) { return s_symbolsByCode[code]; }
		};

		public DateTime ConvertToEasternTime(DateTime dateTime)
		{
			TimeZoneInfo timeZoneInfo;
			try
			{
				timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
			}
			catch (TimeZoneNotFoundException)
			{
				timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
			}

			return TimeZoneInfo.ConvertTimeFromUtc(dateTime, timeZoneInfo);
		}

		public string RenderTemplate(string templateKey, string templateContent, object data)
		{
			if (!_cache.TryGetValue(templateKey, out HandlebarsTemplate<object, string> compiledTemplate))
			{
				compiledTemplate = _globalHandleBars.Compile(templateContent);
				_cache.Set(templateKey, compiledTemplate);
			}

			var result = compiledTemplate(data);
			return result;
		}
	}
}
