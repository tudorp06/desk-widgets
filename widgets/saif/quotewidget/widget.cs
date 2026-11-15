using System;
using System.Drawing;
using DeskWidgets;
using DeskWidgets.Widgets;

[DeskWidget(Name = "Quote", Description = "Daily inspirational quotes")]
public sealed class QuoteWidget : CardWidget
{
    static readonly string[][] Quotes = {
        new[] { "The only way to do great work is to love what you do.", "Steve Jobs" },
        new[] { "In the middle of difficulty lies opportunity.", "Albert Einstein" },
        new[] { "Simplicity is the ultimate sophistication.", "Leonardo da Vinci" },
        new[] { "The best time to plant a tree was 20 years ago. The second best time is now.", "Chinese Proverb" },
        new[] { "Stay hungry, stay foolish.", "Steve Jobs" },
        new[] { "It does not matter how slowly you go as long as you do not stop.", "Confucius" },
        new[] { "Everything you can imagine is real.", "Pablo Picasso" },
        new[] { "What we think, we become.", "Buddha" },
        new[] { "The mind is everything. What you think you become.", "Buddha" },
        new[] { "Do what you can, with what you have, where you are.", "Theodore Roosevelt" },
        new[] { "Creativity is intelligence having fun.", "Albert Einstein" },
        new[] { "Be yourself; everyone else is already taken.", "Oscar Wilde" },
        new[] { "The only impossible journey is the one you never begin.", "Tony Robbins" },
        new[] { "Turn your wounds into wisdom.", "Oprah Winfrey" },
        new[] { "Not all those who wander are lost.", "J.R.R. Tolkien" },
        new[] { "What you do speaks so loudly that I cannot hear what you say.", "Ralph Waldo Emerson" },
        new[] { "Happiness is not something ready made. It comes from your own actions.", "Dalai Lama" },
        new[] { "The purpose of our lives is to be happy.", "Dalai Lama" },
        new[] { "Life is what happens when you're busy making other plans.", "John Lennon" },
        new[] { "Get busy living or get busy dying.", "Stephen King" },
        new[] { "You miss 100% of the shots you don't take.", "Wayne Gretzky" },
        new[] { "Whether you think you can or you think you can't, you're right.", "Henry Ford" },
        new[] { "The best revenge is massive success.", "Frank Sinatra" },
        new[] { "I have not failed. I've just found 10,000 ways that won't work.", "Thomas Edison" },
        new[] { "A person who never made a mistake never tried anything new.", "Albert Einstein" },
        new[] { "The secret of getting ahead is getting started.", "Mark Twain" },
        new[] { "It always seems impossible until it's done.", "Nelson Mandela" },
        new[] { "We are what we repeatedly do. Excellence is not an act, but a habit.", "Aristotle" },
        new[] { "The only limit to our realization of tomorrow is our doubts of today.", "Franklin D. Roosevelt" },
        new[] { "Do one thing every day that scares you.", "Eleanor Roosevelt" },
        new[] { "Well done is better than well said.", "Benjamin Franklin" },
        new[] { "In three words I can sum up everything I've learned about life: it goes on.", "Robert Frost" },
        new[] { "If you want to lift yourself up, lift up someone else.", "Booker T. Washington" },
        new[] { "The unexamined life is not worth living.", "Socrates" },
        new[] { "Imagination is more important than knowledge.", "Albert Einstein" },
        new[] { "Try not to become a man of success. Rather become a man of value.", "Albert Einstein" },
        new[] { "You must be the change you wish to see in the world.", "Mahatma Gandhi" },
        new[] { "The only true wisdom is in knowing you know nothing.", "Socrates" },
        new[] { "I think, therefore I am.", "Rene Descartes" },
        new[] { "An unexamined life is not worth living.", "Socrates" },
        new[] { "To be or not to be, that is the question.", "William Shakespeare" },
        new[] { "That which does not kill us makes us stronger.", "Friedrich Nietzsche" },
        new[] { "The journey of a thousand miles begins with one step.", "Lao Tzu" },
        new[] { "Knowledge is power.", "Francis Bacon" },
        new[] { "A room without books is like a body without a soul.", "Marcus Tullius Cicero" },
        new[] { "No one can make you feel inferior without your consent.", "Eleanor Roosevelt" },
        new[] { "We accept the love we think we deserve.", "Stephen Chbosky" },
        new[] { "Logic will get you from A to Z; imagination will get you everywhere.", "Albert Einstein" },
        new[] { "There is nothing permanent except change.", "Heraclitus" },
        new[] { "The only thing we have to fear is fear itself.", "Franklin D. Roosevelt" },
        new[] { "The best among you are those who have the best manners and character.", "Prophet Muhammad" },
        new[] { "Seek knowledge from the cradle to the grave.", "Prophet Muhammad" },
        new[] { "The strong person is not the one who can wrestle, but the one who controls himself when angry.", "Prophet Muhammad" },
        new[] { "Make things easy, do not make things difficult. Give glad tidings and do not repel people.", "Prophet Muhammad" },
        new[] { "None of you truly believes until he wishes for his brother what he wishes for himself.", "Prophet Muhammad" },
        new[] { "The best of people are those who are most beneficial to people.", "Prophet Muhammad" },
        new[] { "A kind word is a form of charity.", "Prophet Muhammad" },
        new[] { "Do not waste water even if you are at a running stream.", "Prophet Muhammad" },
    };

    int index;

    public QuoteWidget(Section settings) : base(settings, false) { }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        int day = (int)(DateTime.Now.Ticks / TimeSpan.TicksPerDay);
        index = ((day % Quotes.Length) + Quotes.Length) % Quotes.Length;
        int saved = Settings.GetInt("idx", -1);
        if (saved >= 0 && saved < Quotes.Length) index = saved;
    }

    void NextQuote()
    {
        index = (index + 1) % Quotes.Length;
        Settings.Set("idx", index);
        Render();
    }

    void PrevQuote()
    {
        index = (index - 1 + Quotes.Length) % Quotes.Length;
        Settings.Set("idx", index);
        Render();
    }

    void RandomQuote()
    {
        var rng = new Random();
        int next = rng.Next(Quotes.Length - 1);
        if (next >= index) next++;
        index = next;
        Settings.Set("idx", index);
        Render();
    }

    protected override void OnWidgetClick(Point p) { RandomQuote(); }

    protected override void BuildWidgetMenu(PopupMenu menu)
    {
        menu.Add("Next Quote", NextQuote);
        menu.Add("Previous Quote", PrevQuote);
        menu.Add("Random Quote", RandomQuote);
    }

    protected override void DrawContent(Graphics g, Palette pal)
    {
        float m = P(16), w = W - m * 2;
        string[] q = Quotes[index % Quotes.Length];
        string text = "“" + q[0] + "”";
        string author = "— " + q[1];

        DrawQuoteGlyph(g, pal.Purple, m, m + P(1), P(12));
        Label(g, "Quote", F(13, "semibold"), pal.Purple, m + P(17), m - P(1));

        float topArea = m + P(22);
        float bottomArea = H - m - P(16);
        float areaH = bottomArea - topArea;

        Font qf = F(13, "regular");
        SizeF measured = g.MeasureString(text, qf, (int)w);
        float textH = measured.Height;
        float textY = topArea + (areaH - textH) / 2;
        if (textY < topArea) textY = topArea;

        using (var fmt = new StringFormat(StringFormat.GenericTypographic))
        using (var b = new SolidBrush(pal.Fg))
        {
            fmt.Trimming = StringTrimming.EllipsisWord;
            fmt.FormatFlags |= StringFormatFlags.LineLimit;
            g.DrawString(text, F(14, "regular"), b, new RectangleF(m, textY, w, areaH), fmt);
        }

        Label(g, author, F(11, "regular"), pal.Fg2, W - m, H - m - P(1), w, StringAlignment.Far);
    }

    static void DrawQuoteGlyph(Graphics g, Color c, float x, float y, float s)
    {
        using (var b = new SolidBrush(c))
        {
            float qw = s * 0.35f, qh = s * 0.4f, gap = s * 0.12f;
            float y1 = y + s * 0.2f;
            FillRound(g, c, new RectangleF(x, y1, qw, qh), qh * 0.25f);
            FillRound(g, c, new RectangleF(x + qw + gap, y1, qw, qh), qh * 0.25f);
            using (var pen = new Pen(c, Math.Max(1f, s * 0.1f)))
            {
                pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawArc(pen, x - qw * 0.1f, y1 + qh * 0.2f, qw * 0.7f, qh * 1.1f, 180, 90);
                g.DrawArc(pen, x + qw + gap - qw * 0.1f, y1 + qh * 0.2f, qw * 0.7f, qh * 1.1f, 180, 90);
            }
        }
    }
}
