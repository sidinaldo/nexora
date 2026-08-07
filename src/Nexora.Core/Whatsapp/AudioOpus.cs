namespace Nexora.Core.Whatsapp;

/// <summary>===================== O FORMATO DA NOTA DE VOZ (BLOCO 13) =====================
///
/// O WhatsApp so trata como NOTA DE VOZ o audio em OGG com codec Opus. Qualquer outra coisa chega
/// como arquivo anexo: o cliente ve um clipe para baixar em vez do balao com onda e botao de
/// tocar. A mensagem "chega", e mesmo assim esta errada — que e o pior tipo de falha, porque
/// ninguem abre chamado.
///
/// O `MediaRecorder` do navegador nao entrega OGG em todo lugar:
///
///   Firefox        audio/ogg;codecs=opus     -> ja e o que precisamos
///   Chrome/Edge    audio/webm;codecs=opus    -> Opus dentro de OUTRO conteiner
///   Safari/iOS     audio/mp4 (AAC)           -> outro CODEC
///
/// ===================== POR QUE REMUX E NAO FFMPEG =====================
/// O caso do Chrome — que e a maioria — NAO precisa de conversao de audio: os bytes de Opus ja
/// estao la, so estao empacotados em Matroska em vez de OGG. Trocar o conteiner e manipulacao de
/// bytes, sem decodificar nem recodificar: nao perde qualidade, e instantaneo, e nao acrescenta
/// dependencia nenhuma.
///
/// FFmpeg resolveria os tres casos, mas entra no Dockerfile e leva ~80 MB de imagem para
/// converter o que, em 90% das vezes, nao precisa de conversao. Fica registrado como a saida para
/// o Safari — ver docs/BLOCO-13.md.
///
/// SAFARI/iOS: AAC para Opus e transcodificacao de verdade. Sem FFmpeg, nao da. A gravacao e
/// RECUSADA la, com mensagem clara, em vez de mandar um anexo que o cliente nao vai ouvir.
/// ====================================================================================</summary>
public static class AudioOpus
{
    /// <summary>Cinco minutos. Nota de voz mais longa que isso nao e recado, e o teto tambem
    /// protege o disco: cinco minutos de Opus a 24 kbps sao ~900 KB.</summary>
    public static readonly TimeSpan DuracaoMaxima = TimeSpan.FromMinutes(5);

    /// <summary>O mime que sai daqui e vai para a Evolution. Com `codecs=opus` explicito porque
    /// `audio/ogg` sozinho tambem carrega Vorbis, e a Evolution decide o `mediatype` pelo que
    /// recebe.</summary>
    public const string MimeNotaDeVoz = "audio/ogg";

    // ==================================================================== deteccao
    public static bool EhOgg(ReadOnlySpan<byte> b) => Comeca(b, "OggS"u8);

    /// <summary>Matroska/WebM comecam com o mesmo cabecalho EBML (0x1A45DFA3).</summary>
    public static bool EhWebm(ReadOnlySpan<byte> b) => Comeca(b, [0x1A, 0x45, 0xDF, 0xA3]);

    private static bool Comeca(ReadOnlySpan<byte> b, ReadOnlySpan<byte> assinatura) =>
        b.Length >= assinatura.Length && b[..assinatura.Length].SequenceEqual(assinatura);

    /// <summary>Deixa o audio no formato que o WhatsApp trata como nota de voz.
    ///
    /// OGG passa direto; WebM e reempacotado; o resto e recusado com `null` — quem chama traduz
    /// em mensagem para o vendedor.</summary>
    public static byte[]? ParaNotaDeVoz(byte[] conteudo)
    {
        if (EhOgg(conteudo)) return conteudo;
        if (EhWebm(conteudo)) return RemuxarWebmParaOgg(conteudo);
        return null;
    }

    // ==================================================================== duracao
    /// <summary>A duracao de um OGG/Opus, pela GRANULE POSITION da ultima pagina.
    ///
    /// Granule em Opus e a contagem de amostras em 48 kHz — independente da taxa real da
    /// gravacao, por definicao do formato. Descontado o `pre-skip` do cabecalho, que sao amostras
    /// de aquecimento do decodificador e nao som.
    ///
    /// Ler a ultima pagina e barato (varre de tras para frente procurando "OggS") e nao exige
    /// decodificar nada.</summary>
    public static TimeSpan? DuracaoDe(byte[] ogg)
    {
        if (!EhOgg(ogg)) return null;

        var preSkip = PreSkipDe(ogg);

        // De tras para frente ate achar o inicio da ultima pagina.
        for (var i = ogg.Length - 27; i >= 0; i--)
        {
            if (ogg[i] != (byte)'O' || ogg[i + 1] != (byte)'g'
                || ogg[i + 2] != (byte)'g' || ogg[i + 3] != (byte)'S') continue;

            var granule = BitConverter.ToInt64(ogg, i + 6);
            if (granule <= 0) continue;

            var amostras = Math.Max(0, granule - preSkip);
            return TimeSpan.FromSeconds(amostras / 48000.0);
        }
        return null;
    }

    /// <summary>`pre-skip` mora no OpusHead, bytes 10-11 (little-endian), da primeira pagina.</summary>
    private static int PreSkipDe(byte[] ogg)
    {
        var i = Procurar(ogg, "OpusHead"u8);
        return i >= 0 && i + 12 <= ogg.Length ? BitConverter.ToUInt16(ogg, i + 10) : 0;
    }

    private static int Procurar(byte[] onde, ReadOnlySpan<byte> o)
    {
        for (var i = 0; i + o.Length <= onde.Length; i++)
            if (onde.AsSpan(i, o.Length).SequenceEqual(o)) return i;
        return -1;
    }

    // ==================================================================== remux
    /// <summary>WebM/Opus -> OGG/Opus. TROCA DE CONTEINER, sem decodificar.
    ///
    /// Le os pacotes de Opus dos `SimpleBlock` do Matroska e os reescreve em paginas OGG,
    /// precedidos dos dois cabecalhos que o formato exige (OpusHead e OpusTags).
    ///
    /// Devolve `null` quando o arquivo nao e o que se espera — melhor recusar que produzir um OGG
    /// invalido que a Evolution aceita e o celular nao toca.</summary>
    public static byte[]? RemuxarWebmParaOgg(byte[] webm)
    {
        var (cabecalho, pacotes) = LerWebm(webm);
        if (pacotes.Count == 0) return null;

        // Sem CodecPrivate — ou com um vazio/curto demais, que da no mesmo — monta um OpusHead
        // padrao: mono, 48 kHz, pre-skip de 3840 amostras (80 ms), que e o valor que os
        // codificadores usam. Desistir seria pior: sem OpusHead o arquivo nao toca em lugar
        // nenhum, e o padrao acerta o caso comum.
        //
        // 19 bytes e o tamanho MINIMO de um OpusHead (magica de 8 + 11 de campos).
        if (cabecalho is null || cabecalho.Length < 19
            || !cabecalho.AsSpan(0, 8).SequenceEqual("OpusHead"u8))
        {
            cabecalho = [.. "OpusHead"u8, 1, 1, 0x00, 0x0F, 0x80, 0xBB, 0x00, 0x00, 0x00, 0x00, 0];
        }

        var saida = new MemoryStream();
        var serial = 0x4E58_0A01;   // qualquer valor; identifica o fluxo dentro do arquivo
        var seq = 0;

        // Pagina 1: OpusHead, marcada como INICIO DO FLUXO.
        EscreverPagina(saida, cabecalho, granule: 0, serial, seq++, tipo: 0x02);

        // Pagina 2: OpusTags. O formato EXIGE o cabecalho de comentarios, mesmo vazio — sem ele
        // o decodificador recusa o fluxo.
        byte[] tags = [.. "OpusTags"u8, 6, 0, 0, 0, .. "Nexora"u8, 0, 0, 0, 0];
        EscreverPagina(saida, tags, granule: 0, serial, seq++, tipo: 0x00);

        // Paginas de audio. Ate 255 segmentos por pagina, e um pacote nunca e partido entre
        // paginas aqui: pacote de Opus cabe folgado em 255 * 255 bytes.
        var lote = new List<byte[]>();
        long amostras = 0;
        var segmentos = 0;

        void Descarregar(bool fim)
        {
            if (lote.Count == 0) return;
            EscreverPagina(saida, [.. lote.SelectMany(p => p)], amostras, serial, seq++,
                tipo: (byte)(fim ? 0x04 : 0x00), lote);
            lote.Clear();
            segmentos = 0;
        }

        foreach (var p in pacotes)
        {
            var precisa = p.Length / 255 + 1;
            if (segmentos + precisa > 255) Descarregar(false);

            lote.Add(p);
            segmentos += precisa;
            amostras += AmostrasNoPacote(p);
        }
        Descarregar(true);

        return saida.ToArray();
    }

    /// <summary>Quantas amostras (em 48 kHz) um pacote de Opus carrega, pelo byte TOC.
    ///
    /// Sem isto a granule position sairia errada, e o player mostraria a duracao errada — ou o
    /// WhatsApp cortaria o audio no meio.</summary>
    private static int AmostrasNoPacote(byte[] pacote)
    {
        if (pacote.Length == 0) return 0;

        var toc = pacote[0];
        var config = toc >> 3;

        // Duracao do QUADRO, em amostras de 48 kHz.
        var quadro = config switch
        {
            < 12 => new[] { 480, 960, 1920, 2880 }[config % 4],           // SILK: 10/20/40/60 ms
            < 16 => (config % 2) == 0 ? 480 : 960,                        // Hibrido: 10/20 ms
            _ => new[] { 120, 240, 480, 960 }[config % 4]                 // CELT: 2.5/5/10/20 ms
        };

        // Quantos quadros no pacote (codigo nos 2 bits baixos do TOC).
        var quantos = (toc & 0x03) switch
        {
            0 => 1,
            1 or 2 => 2,
            _ => pacote.Length > 1 ? pacote[1] & 0x3F : 1   // codigo 3: contagem no byte seguinte
        };

        return quadro * quantos;
    }

    // ---------------------------------------------------------------- OGG
    private static void EscreverPagina(
        Stream saida, byte[] dados, long granule, int serial, int seq, byte tipo,
        List<byte[]>? pacotes = null)
    {
        // A TABELA DE SEGMENTOS descreve o tamanho de cada pacote em pedacos de ate 255 bytes.
        // Um pacote de 600 bytes vira 255, 255, 90 — e o 90 (< 255) e o que marca o FIM dele.
        // Sem essa marca o decodificador junta dois pacotes num so.
        var tabela = new List<byte>();
        foreach (var p in pacotes ?? [dados])
        {
            var resto = p.Length;
            while (resto >= 255) { tabela.Add(255); resto -= 255; }
            tabela.Add((byte)resto);
        }

        var pagina = new byte[27 + tabela.Count + dados.Length];
        "OggS"u8.CopyTo(pagina);
        pagina[4] = 0;             // versao
        pagina[5] = tipo;          // 0x02 inicio, 0x04 fim
        BitConverter.GetBytes(granule).CopyTo(pagina, 6);
        BitConverter.GetBytes(serial).CopyTo(pagina, 14);
        BitConverter.GetBytes(seq).CopyTo(pagina, 18);
        // 22..25 = CRC, calculado com o campo ZERADO — por isso ele fica para o fim.
        pagina[26] = (byte)tabela.Count;
        tabela.CopyTo(pagina, 27);
        dados.CopyTo(pagina, 27 + tabela.Count);

        BitConverter.GetBytes(Crc(pagina)).CopyTo(pagina, 22);
        saida.Write(pagina);
    }

    /// <summary>CRC-32 do OGG. NAO e o CRC-32 comum: mesmo polinomio (0x04C11DB7), mas SEM
    /// reflexao de bits e sem XOR final. Usar o do zip produz um arquivo que todo player
    /// recusa — e o erro nao diz "CRC", diz "arquivo corrompido".</summary>
    private static uint Crc(ReadOnlySpan<byte> dados)
    {
        uint crc = 0;
        foreach (var b in dados)
        {
            crc ^= (uint)b << 24;
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x8000_0000) != 0 ? (crc << 1) ^ 0x04C1_1DB7 : crc << 1;
        }
        return crc;
    }

    // ---------------------------------------------------------------- Matroska
    /// <summary>Extrai o OpusHead (CodecPrivate) e os pacotes de audio de um WebM.
    ///
    /// Le so o que interessa: `Tracks` para o cabecalho e `Cluster`/`SimpleBlock` para os
    /// pacotes. Tudo o mais e pulado pelo tamanho declarado, sem interpretar.</summary>
    private static (byte[]? Cabecalho, List<byte[]> Pacotes) LerWebm(byte[] b)
    {
        byte[]? cabecalho = null;
        var pacotes = new List<byte[]>();

        // Os elementos que precisam ser ABERTOS em vez de pulados.
        var mestres = new HashSet<ulong> { 0x18538067, 0x1654AE6B, 0xAE, 0x1F43B675, 0xA0 };

        void Andar(int inicio, int fim)
        {
            var i = inicio;
            while (i < fim)
            {
                if (!LerId(b, ref i, out var id)) return;
                if (!LerTamanho(b, ref i, out var tamanho)) return;

                // Tamanho desconhecido (todos os bits em 1): comum em stream ao vivo. Segue
                // andando dentro dele em vez de desistir.
                var conteudoFim = tamanho == ulong.MaxValue ? fim : (int)Math.Min(fim, i + (long)tamanho);

                if (mestres.Contains(id))
                {
                    Andar(i, conteudoFim);
                }
                else if (id is 0x63A2 && cabecalho is null)   // CodecPrivate
                {
                    cabecalho = b[i..conteudoFim];
                }
                else if (id is 0xA3 or 0xA1)                 // SimpleBlock / Block
                {
                    var j = i;
                    if (LerTamanho(b, ref j, out _) && j + 3 <= conteudoFim)
                    {
                        // 2 bytes de timecode + 1 de flags, e o resto e o pacote de Opus.
                        // Lacing (flags & 0x06) nao aparece em audio do MediaRecorder; se
                        // aparecesse, o pacote sairia concatenado e o remux seria recusado
                        // adiante pela duracao absurda.
                        var dados = b[(j + 3)..conteudoFim];
                        if (dados.Length > 0) pacotes.Add(dados);
                    }
                }

                i = conteudoFim;
                if (conteudoFim <= inicio) return;   // trava contra arquivo malformado
            }
        }

        try { Andar(0, b.Length); }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return (null, []);   // arquivo truncado ou corrompido: recusa, nao adivinha
        }

        return (cabecalho, pacotes);
    }

    /// <summary>ID de elemento EBML: o primeiro bit 1 diz quantos bytes ele ocupa, e os bytes
    /// vao INTEIROS (o marcador faz parte do id).</summary>
    private static bool LerId(byte[] b, ref int i, out ulong id)
    {
        id = 0;
        if (i >= b.Length) return false;

        var largura = Largura(b[i]);
        if (largura == 0 || i + largura > b.Length) return false;

        for (var k = 0; k < largura; k++) id = (id << 8) | b[i + k];
        i += largura;
        return true;
    }

    /// <summary>Tamanho EBML: mesmo esquema, mas o bit marcador SAI do valor.</summary>
    private static bool LerTamanho(byte[] b, ref int i, out ulong tamanho)
    {
        tamanho = 0;
        if (i >= b.Length) return false;

        var largura = Largura(b[i]);
        if (largura == 0 || i + largura > b.Length) return false;

        ulong v = (ulong)(b[i] & (0xFF >> largura));
        var todosUm = v == (ulong)(0xFF >> largura);

        for (var k = 1; k < largura; k++)
        {
            v = (v << 8) | b[i + k];
            todosUm &= b[i + k] == 0xFF;
        }

        i += largura;
        tamanho = todosUm ? ulong.MaxValue : v;
        return true;
    }

    private static int Largura(byte primeiro)
    {
        for (var l = 1; l <= 8; l++)
            if ((primeiro & (0x80 >> (l - 1))) != 0) return l;
        return 0;
    }
}
