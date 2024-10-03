using HandlebarsDotNet;
using HandlebarsDotNet.IO;
using System;
using System.Globalization;

namespace PdfApi.Formatters
{
	public sealed class DateTimeFormatter : IFormatter, IFormatterProvider
	{
		private readonly string _format;

		public DateTimeFormatter(string format) => _format = format;

		public void Format<T>(T value, in EncodedTextWriter writer)
		{
			if (value is not DateTime dateTime)
				throw new ArgumentException("supposed to be DateTime");

			writer.Write($"{string.Format("{0}{1} {2}", dateTime.Day, GetDaySuffix(dateTime.Day), dateTime.ToString("MMMM yyyy, hh:mmtt", CultureInfo.InvariantCulture))}");
		}

		public bool TryCreateFormatter(Type type, out IFormatter formatter)
		{
			if (type != typeof(DateTime))
			{
				formatter = null;
				return false;
			}

			formatter = this;
			return true;
		}

		private static string GetDaySuffix(int day)
		{
			return day switch
			{
				1 or 21 or 31 => "st",
				2 or 22 => "nd",
				3 or 23 => "rd",
				_ => "th",
			};
		}
	}
}
