namespace RadSharp.CodeEditing;

/// <summary>
/// An append-only block of text that pieces point into, with the positions of its line feeds. The writer only
/// ever appends; a reader on another thread only reads the prefix its pieces reference, which was written
/// before the snapshot holding those pieces was published, so no locking is needed.
/// </summary>
sealed class Chunk
{
    public char[] Chars;
    public int Length;
    public int[] Lfs;
    public int LfCount;

    public Chunk(int capacity)
    {
        Chars = new char[Math.Max(16, capacity)];
        Lfs = new int[16];
    }

    public Chunk(string text)
    {
        Chars = text.ToCharArray();
        Length = text.Length;
        int count = 0;
        foreach (char c in Chars) if (c == '\n') count++;
        Lfs = new int[Math.Max(1, count)];
        for (int i = 0; i < Chars.Length; i++) if (Chars[i] == '\n') Lfs[LfCount++] = i;
    }

    public int Free => Chars.Length - Length;

    /// <summary>Appends <paramref name="text"/> (which must fit) and returns the piece covering it.</summary>
    public Piece Append(ReadOnlySpan<char> text)
    {
        int start = Length, firstLf = LfCount;
        text.CopyTo(Chars.AsSpan(start));
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            if (LfCount == Lfs.Length)
            {
                var grown = new int[Lfs.Length * 2];
                Array.Copy(Lfs, grown, LfCount);
                Lfs = grown;   // readers holding the old array still see the prefix they need
            }
            Lfs[LfCount++] = start + i;
        }
        Length += text.Length;
        return new Piece(this, start, text.Length, firstLf, LfCount - firstLf);
    }

    /// <summary>Index into <see cref="Lfs"/> of the first line feed at or after <paramref name="pos"/>, searching [lo, hi).</summary>
    public int LowerBound(int lo, int hi, int pos)
    {
        var lfs = Lfs;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (lfs[mid] < pos) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}

/// <summary>A run of text: <see cref="Length"/> characters of <see cref="Chunk"/> from <see cref="Start"/>.</summary>
readonly struct Piece(Chunk chunk, int start, int length, int firstLf, int lfCount)
{
    public readonly Chunk Chunk = chunk;
    public readonly int Start = start;
    public readonly int Length = length;
    /// <summary>Index into the chunk's line-feed table of this piece's first line feed.</summary>
    public readonly int FirstLf = firstLf;
    public readonly int LfCount = lfCount;

    public int End => Start + Length;

    /// <summary>Line feeds in the first <paramref name="local"/> characters of the piece.</summary>
    public int LfsBefore(int local) =>
        local <= 0 ? 0 : local >= Length ? LfCount : Chunk.LowerBound(FirstLf, FirstLf + LfCount, Start + local) - FirstLf;

    /// <summary>Piece-local offset just after the <paramref name="k"/>-th (1-based) line feed in this piece.</summary>
    public int AfterLf(int k) => Chunk.Lfs[FirstLf + k - 1] - Start + 1;

    public (Piece Left, Piece Right) SplitAt(int local)
    {
        int before = LfsBefore(local);
        return (new Piece(Chunk, Start, local, FirstLf, before),
                new Piece(Chunk, Start + local, Length - local, FirstLf + before, LfCount - before));
    }

    public ReadOnlySpan<char> Span => Chunk.Chars.AsSpan(Start, Length);
}

/// <summary>
/// An immutable (persistent) AVL tree of pieces in document order. Every node knows its subtree's character and
/// line-feed totals, which makes the tree the line index too: edits, offset → line and line → offset are all
/// O(log n). Edits copy only the path they touch, so older roots (snapshots) stay valid and share structure.
/// </summary>
sealed class Node
{
    public readonly Node? Left, Right;
    public readonly Piece Piece;
    public readonly int Height, Length, Lfs, Count;

    Node(Node? left, Piece piece, Node? right)
    {
        Left = left;
        Right = right;
        Piece = piece;
        Height = 1 + Math.Max(H(left), H(right));
        Length = Len(left) + piece.Length + Len(right);
        Lfs = (left?.Lfs ?? 0) + piece.LfCount + (right?.Lfs ?? 0);
        Count = (left?.Count ?? 0) + 1 + (right?.Count ?? 0);
    }

    static int H(Node? n) => n?.Height ?? 0;
    public static int Len(Node? n) => n?.Length ?? 0;
    public static int LfsOf(Node? n) => n?.Lfs ?? 0;

    static Node Make(Node? l, Piece p, Node? r) => new(l, p, r);

    static Node RotateRight(Node n) => Make(n.Left!.Left, n.Left.Piece, Make(n.Left.Right, n.Piece, n.Right));
    static Node RotateLeft(Node n) => Make(Make(n.Left, n.Piece, n.Right!.Left), n.Right.Piece, n.Right.Right);

    static Node Balance(Node? l, Piece p, Node? r)
    {
        int bf = H(l) - H(r);
        if (bf > 1)
        {
            if (H(l!.Left) < H(l.Right)) l = RotateLeft(l);
            return RotateRight(Make(l, p, r));
        }
        if (bf < -1)
        {
            if (H(r!.Right) < H(r.Left)) r = RotateRight(r);
            return RotateLeft(Make(l, p, r));
        }
        return Make(l, p, r);
    }

    /// <summary>All of <paramref name="l"/>, then <paramref name="p"/>, then all of <paramref name="r"/>, balanced.</summary>
    public static Node Join(Node? l, Piece p, Node? r)
    {
        int hl = H(l), hr = H(r);
        if (hl > hr + 1) return Balance(l!.Left, l.Piece, Join(l.Right, p, r));
        if (hr > hl + 1) return Balance(Join(l, p, r!.Left), r.Piece, r.Right);
        return Make(l, p, r);
    }

    public static Node? Concat(Node? l, Node? r)
    {
        if (l == null) return r;
        if (r == null) return l;
        var (rest, last) = SplitLast(l);
        return Join(rest, last, r);
    }

    public static (Node? Remainder, Piece Last) SplitLast(Node n)
    {
        if (n.Right == null) return (n.Left, n.Piece);
        var (rest, last) = SplitLast(n.Right);
        return (Join(n.Left, n.Piece, rest), last);
    }

    /// <summary>Splits into the text before <paramref name="offset"/> and the text from it on.</summary>
    public static (Node? Left, Node? Right) Split(Node? n, int offset)
    {
        if (n == null) return (null, null);
        int leftLen = Len(n.Left);
        if (offset <= leftLen)
        {
            if (offset == 0 && n.Left == null) return (null, n);
            var (ll, lr) = Split(n.Left, offset);
            return (ll, Join(lr, n.Piece, n.Right));
        }
        int local = offset - leftLen;
        if (local >= n.Piece.Length)
        {
            if (local == n.Piece.Length && n.Right == null) return (n, null);
            var (rl, rr) = Split(n.Right, local - n.Piece.Length);
            return (Join(n.Left, n.Piece, rl), rr);
        }
        var (p1, p2) = n.Piece.SplitAt(local);
        return (Join(n.Left, p1, null), Join(null, p2, n.Right));
    }

    // ---- Queries ----

    public static char CharAt(Node n, int offset)
    {
        while (true)
        {
            int leftLen = Len(n.Left);
            if (offset < leftLen) { n = n.Left!; continue; }
            offset -= leftLen;
            if (offset < n.Piece.Length) return n.Piece.Chunk.Chars[n.Piece.Start + offset];
            offset -= n.Piece.Length;
            n = n.Right!;
        }
    }

    /// <summary>Copies the text from <paramref name="start"/> into <paramref name="dest"/> (its length is the count).</summary>
    public static void CopyTo(Node? n, int start, Span<char> dest)
    {
        while (n != null && dest.Length > 0)
        {
            int leftLen = Len(n.Left);
            if (start < leftLen)
            {
                int fromLeft = Math.Min(dest.Length, leftLen - start);
                CopyTo(n.Left, start, dest[..fromLeft]);
                dest = dest[fromLeft..];
                start = leftLen;
                if (dest.Length == 0) return;
            }
            int local = start - leftLen;
            if (local < n.Piece.Length)
            {
                int take = Math.Min(dest.Length, n.Piece.Length - local);
                n.Piece.Span.Slice(local, take).CopyTo(dest);
                dest = dest[take..];
                start += take;
            }
            start -= leftLen + n.Piece.Length;
            n = n.Right;
        }
    }

    /// <summary>The number of line feeds before <paramref name="offset"/>: the 0-based line the offset is on.</summary>
    public static int LfsBefore(Node? n, int offset)
    {
        int count = 0;
        while (n != null)
        {
            int leftLen = Len(n.Left);
            if (offset <= leftLen) { n = n.Left; continue; }
            count += LfsOf(n.Left);
            offset -= leftLen;
            if (offset <= n.Piece.Length) return count + n.Piece.LfsBefore(offset);
            count += n.Piece.LfCount;
            offset -= n.Piece.Length;
            n = n.Right;
        }
        return count;
    }

    /// <summary>The offset just after the <paramref name="k"/>-th (1-based) line feed: where line <paramref name="k"/> starts.</summary>
    public static int OffsetAfterLf(Node n, int k)
    {
        int acc = 0;
        while (true)
        {
            int leftLfs = LfsOf(n.Left);
            if (k <= leftLfs) { n = n.Left!; continue; }
            k -= leftLfs;
            acc += Len(n.Left);
            if (k <= n.Piece.LfCount) return acc + n.Piece.AfterLf(k);
            k -= n.Piece.LfCount;
            acc += n.Piece.Length;
            n = n.Right!;
        }
    }

    public static void CollectPieces(Node? n, List<Piece> into)
    {
        if (n == null) return;
        CollectPieces(n.Left, into);
        into.Add(n.Piece);
        CollectPieces(n.Right, into);
    }
}
