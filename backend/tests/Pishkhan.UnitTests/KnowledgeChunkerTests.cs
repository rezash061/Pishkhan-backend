using Pishkhan.Modules.Knowledge.Services;

namespace Pishkhan.UnitTests;

public sealed class KnowledgeChunkerTests
{
    [Fact]
    public void Persian_sentences_end_at_question_marks_periods_and_line_breaks()
    {
        Chunker.Sentences("سلام. ارسال دارید؟ بله!\nقیمت ۱۰۰ تومان").ShouldBe(["سلام.", "ارسال دارید؟", "بله!", "قیمت ۱۰۰ تومان"]);
    }

    [Fact]
    public void Long_texts_split_into_chunks_within_the_budget_that_overlap()
    {
        var text = string.Join(' ', Enumerable.Range(1, 100).Select(i => $"جمله شماره {i} درباره شرایط ارسال و مرجوعی کالا است."));
        var chunks = Chunker.Split(text);

        chunks.Count.ShouldBeGreaterThan(2);
        chunks.ShouldAllBe(c => c.TokenCount <= Chunker.MaxTokens);
        for (var i = 1; i < chunks.Count; i++)
        {
            // The next chunk starts inside the previous one and repeats its last sentence.
            chunks[i - 1].Text.ShouldContain(Chunker.Sentences(chunks[i].Text)[0]);
            chunks[i].Text.ShouldContain(Chunker.Sentences(chunks[i - 1].Text)[^1]);
        }

        string.Join(' ', chunks.Select(c => c.Text)).ShouldContain("جمله شماره 100 ");
    }

    [Fact]
    public void A_sentence_longer_than_a_chunk_is_cut_by_words()
    {
        var chunks = Chunker.Split(string.Join(' ', Enumerable.Repeat("کلمه", 2_000)));
        chunks.Count.ShouldBeGreaterThan(1);
        chunks.ShouldAllBe(c => c.TokenCount <= Chunker.MaxTokens);
    }

    [Fact]
    public void Search_queries_keep_only_words_so_tsquery_syntax_cannot_be_injected()
    {
        KnowledgeSearch.TsQuery("ارسال & (شهرستان) | !مشهد:*").ShouldBe("ارسال | شهرستان | مشهد");
        KnowledgeSearch.TsQuery("؟ ! .").ShouldBeNull();
    }
}
