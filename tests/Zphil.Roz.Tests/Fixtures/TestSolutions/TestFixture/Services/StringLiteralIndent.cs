namespace TestFixture.Services
{
	public class StringLiteralIndent
	{
		public string Verbatim()
		{
			return @"
        SELECT *
          FROM Table
         WHERE Id = 1";
		}

		public int Anchor()
		{
			return 0;
		}
	}
}
