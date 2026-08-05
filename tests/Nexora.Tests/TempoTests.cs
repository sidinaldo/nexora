using Nexora.Core.Tempo;

namespace Nexora.Tests;

/// <summary>A camada de tempo é PURA — nada aqui toca banco. É de propósito: a regra que decide
/// se o semáforo acende e para que dia o follow-up desliza tem que ser verificável sem
/// infraestrutura, senão ninguém a verifica.</summary>
public class CalculadoraFeriadosTests
{
    /// <summary>Páscoas conhecidas. É o número do qual TODOS os feriados móveis dependem:
    /// carnaval, sexta-feira santa e Corpus Christi saem daqui. Errar a Páscoa por um dia
    /// desloca quatro feriados sem que nada quebre visivelmente.</summary>
    [Theory]
    [InlineData(2025, 4, 20)]
    [InlineData(2026, 4, 5)]
    [InlineData(2027, 3, 28)]
    [InlineData(2028, 4, 16)]
    [InlineData(2029, 4, 1)]
    [InlineData(2030, 4, 21)]
    public void Pascoa_bate_com_o_calendario_real(int ano, int mes, int dia)
    {
        Assert.Equal(new DateOnly(ano, mes, dia), CalculadoraFeriados.Pascoa(ano));
    }

    [Fact]
    public void Pascoa_cai_sempre_num_domingo()
    {
        for (var ano = 2024; ano <= 2040; ano++)
            Assert.Equal(DayOfWeek.Sunday, CalculadoraFeriados.Pascoa(ano).DayOfWeek);
    }

    [Theory]
    [InlineData(2026, 2, 16, "Carnaval")]           // segunda de carnaval
    [InlineData(2026, 2, 17, "Carnaval")]           // terça
    [InlineData(2026, 4, 3, "Sexta-feira Santa")]
    [InlineData(2026, 6, 4, "Corpus Christi")]
    public void Feriados_moveis_de_2026_saem_no_dia_certo(int ano, int mes, int dia, string nome)
    {
        var alvo = new DateOnly(ano, mes, dia);
        Assert.Contains(CalculadoraFeriados.Nacionais(ano), f => f.Data == alvo && f.Nome == nome);
    }

    [Fact]
    public void Consciencia_negra_esta_na_lista_nacional()
    {
        // Virou feriado NACIONAL em 2024 (Lei 14.759/2023). Antes disso era municipal em algumas
        // cidades — é o feriado mais fácil de esquecer numa lista copiada de tutorial antigo.
        Assert.Contains(CalculadoraFeriados.Nacionais(2026),
            f => f.Data == new DateOnly(2026, 11, 20));
    }

    [Fact]
    public void Sexta_santa_e_sempre_sexta_e_corpus_christi_sempre_quinta()
    {
        for (var ano = 2024; ano <= 2035; ano++)
        {
            var nacionais = CalculadoraFeriados.Nacionais(ano);
            var santa = nacionais.Single(f => f.Nome == "Sexta-feira Santa").Data;
            var corpus = nacionais.Single(f => f.Nome == "Corpus Christi").Data;

            Assert.Equal(DayOfWeek.Friday, santa.DayOfWeek);
            Assert.Equal(DayOfWeek.Thursday, corpus.DayOfWeek);
        }
    }

    [Fact]
    public void Estaduais_de_uf_desconhecida_devolve_vazio_em_vez_de_lancar()
    {
        // O seed varre empresas; uma UF sem lista não pode derrubar a rodada.
        Assert.Empty(CalculadoraFeriados.Estaduais(2026, "ZZ"));
        Assert.Empty(CalculadoraFeriados.Estaduais(2026, ""));
        Assert.Empty(CalculadoraFeriados.Estaduais(2026, null!));
        Assert.Single(CalculadoraFeriados.Estaduais(2026, "rn"));   // case-insensitive
    }
}

public class CalendarioAtendimentoTests
{
    private const short SegASab = 126;   // bits 1..6
    private const short SegASex = 62;    // bits 1..5

    private static readonly IReadOnlySet<DateOnly> SemFeriado = new HashSet<DateOnly>();

    [Fact]
    public void Domingo_esta_fora_da_mascara_seg_a_sab()
    {
        var domingo = new DateOnly(2026, 8, 2);
        Assert.Equal(DayOfWeek.Sunday, domingo.DayOfWeek);
        Assert.False(CalendarioAtendimento.DiaPermitido(domingo, SegASab, SemFeriado));
        Assert.True(CalendarioAtendimento.DiaPermitido(domingo.AddDays(1), SegASab, SemFeriado));
    }

    [Fact]
    public void Sabado_entra_em_seg_a_sab_e_sai_em_seg_a_sex()
    {
        var sabado = new DateOnly(2026, 8, 8);
        Assert.Equal(DayOfWeek.Saturday, sabado.DayOfWeek);
        Assert.True(CalendarioAtendimento.DiaPermitido(sabado, SegASab, SemFeriado));
        Assert.False(CalendarioAtendimento.DiaPermitido(sabado, SegASex, SemFeriado));
    }

    [Fact]
    public void Feriado_bloqueia_dia_util()
    {
        // 7 de setembro de 2026 é uma SEGUNDA — dia ligado no bitmask e ainda assim fechado.
        var independencia = new DateOnly(2026, 9, 7);
        Assert.Equal(DayOfWeek.Monday, independencia.DayOfWeek);

        Assert.True(CalendarioAtendimento.DiaPermitido(independencia, SegASab, SemFeriado));
        Assert.False(CalendarioAtendimento.DiaPermitido(
            independencia, SegASab, new HashSet<DateOnly> { independencia }));
    }

    [Fact]
    public void Follow_up_de_domingo_desliza_para_segunda()
    {
        var domingo = new DateOnly(2026, 8, 2);
        Assert.Equal(new DateOnly(2026, 8, 3),
            CalendarioAtendimento.ProximaDataPermitida(domingo, SegASab, SemFeriado));
    }

    [Fact]
    public void Emenda_de_feriado_desliza_ate_o_primeiro_dia_aberto()
    {
        // Sexta feriado + fim de semana com atendimento só seg-sex: cai na segunda.
        var sexta = new DateOnly(2026, 4, 3);   // Sexta-feira Santa
        Assert.Equal(DayOfWeek.Friday, sexta.DayOfWeek);

        var resultado = CalendarioAtendimento.ProximaDataPermitida(
            sexta, SegASex, new HashSet<DateOnly> { sexta });

        Assert.Equal(new DateOnly(2026, 4, 6), resultado);
        Assert.Equal(DayOfWeek.Monday, resultado.DayOfWeek);
    }

    [Fact]
    public void Data_ja_permitida_nao_desliza()
    {
        var terca = new DateOnly(2026, 8, 4);
        Assert.Equal(terca, CalendarioAtendimento.ProximaDataPermitida(terca, SegASab, SemFeriado));
    }

    [Fact]
    public void Bitmask_zerado_nao_trava_o_motor()
    {
        // Dado ruim (empresa configurada sem nenhum dia) faria o laço rodar para sempre. A trava
        // de 370 iterações devolve uma data e deixa o problema VISÍVEL — o motor não pode ficar
        // pendurado num laço infinito dentro de um BackgroundService.
        var partida = new DateOnly(2026, 8, 4);
        var resultado = CalendarioAtendimento.ProximaDataPermitida(partida, 0, SemFeriado);

        Assert.Equal(partida.AddDays(370), resultado);
    }
}

public class FusoDeNegocioTests
{
    [Fact]
    public void Resolver_com_id_valido_devolve_o_fuso_do_sistema()
    {
        var fuso = FusoDeNegocio.Resolver(FusoDeNegocio.PadraoBrasil);
        Assert.Equal(TimeSpan.FromHours(-3), fuso.GetUtcOffset(new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Fuso/Que_Nao_Existe")]
    public void Resolver_com_id_ruim_cai_no_fixo_de_Brasilia_em_vez_de_lancar(string? id)
    {
        // Uma empresa com fuso digitado errado NÃO pode derrubar a rodada de todas as outras —
        // e nem rodar em UTC em silêncio, que dispararia follow-up às 5h da manhã.
        var fuso = FusoDeNegocio.Resolver(id);
        Assert.Equal(TimeSpan.FromHours(-3),
            fuso.GetUtcOffset(new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void AgoraNo_converte_o_instante_UTC_para_a_hora_de_parede()
    {
        var relogio = new RelogioFalso(new DateTimeOffset(2026, 8, 4, 23, 30, 0, TimeSpan.Zero));
        var agora = FusoDeNegocio.AgoraNo(relogio, FusoDeNegocio.Resolver(FusoDeNegocio.PadraoBrasil));

        // 23h30 UTC = 20h30 em Brasília, e — crucialmente — ainda é DIA 4, não dia 5. É esse
        // off-by-one que faz o follow-up nascer com a data-alvo errada.
        Assert.Equal(new DateTime(2026, 8, 4, 20, 30, 0), agora);
        Assert.Equal(new DateOnly(2026, 8, 4), DateOnly.FromDateTime(agora));
    }
}

public class TempoUtilTests
{
    private static readonly JanelaAtendimento Comercial = new(8, 20, 126);   // 8h-20h, seg-sáb
    private static readonly IReadOnlySet<DateOnly> SemFeriado = new HashSet<DateOnly>();

    [Fact]
    public void Dentro_do_mesmo_dia_conta_o_tempo_de_parede()
    {
        var inicio = new DateTime(2026, 8, 4, 10, 0, 0);
        var fim = new DateTime(2026, 8, 4, 12, 30, 0);
        Assert.Equal(150, TempoUtil.MinutosUteis(inicio, fim, Comercial, SemFeriado));
    }

    [Fact]
    public void MENSAGEM_DA_NOITE_NAO_AMANHECE_VERMELHA()
    {
        // O TESTE QUE JUSTIFICA A CAMADA INTEIRA. Chegou às 23h de terça; são 8h30 de quarta.
        // No relógio de parede são 9h30 de espera — o que pintaria a conversa de vermelho na
        // primeira coisa que o vendedor vê ao abrir o sistema.
        //
        // Descontando o que estava fechado: 30 minutos. Amarelo acende aos 60.
        var chegou = new DateTime(2026, 8, 4, 23, 0, 0);
        var agora = new DateTime(2026, 8, 5, 8, 30, 0);

        Assert.Equal(30, TempoUtil.MinutosUteis(chegou, agora, Comercial, SemFeriado));
        Assert.Equal(570, (int)(agora - chegou).TotalMinutes);   // o que seria SEM o desconto
    }

    [Fact]
    public void Mensagem_de_19h50_tem_10_minutos_na_abertura_do_dia_seguinte()
    {
        // O exemplo do comentário do TempoUtil, verificado.
        var chegou = new DateTime(2026, 8, 4, 19, 50, 0);
        var agora = new DateTime(2026, 8, 5, 8, 0, 0);
        Assert.Equal(10, TempoUtil.MinutosUteis(chegou, agora, Comercial, SemFeriado));
    }

    [Fact]
    public void Domingo_inteiro_nao_conta()
    {
        // Sábado 19h -> segunda 9h. Conta 1h de sábado + 1h de segunda; o domingo some.
        var chegou = new DateTime(2026, 8, 8, 19, 0, 0);    // sábado
        var agora = new DateTime(2026, 8, 10, 9, 0, 0);     // segunda
        Assert.Equal(DayOfWeek.Saturday, chegou.DayOfWeek);

        Assert.Equal(120, TempoUtil.MinutosUteis(chegou, agora, Comercial, SemFeriado));
    }

    [Fact]
    public void Feriado_no_meio_da_espera_e_descontado()
    {
        // Segunda 19h -> quarta 9h, com a TERÇA feriado. Sem o feriado seriam 1h + 12h + 1h.
        var chegou = new DateTime(2026, 8, 3, 19, 0, 0);
        var agora = new DateTime(2026, 8, 5, 9, 0, 0);
        var feriados = new HashSet<DateOnly> { new(2026, 8, 4) };

        Assert.Equal(60 + 720 + 60, TempoUtil.MinutosUteis(chegou, agora, Comercial, SemFeriado));
        Assert.Equal(60 + 60, TempoUtil.MinutosUteis(chegou, agora, Comercial, feriados));
    }

    [Fact]
    public void Fim_antes_do_inicio_devolve_zero_em_vez_de_negativo()
    {
        // Acontece de verdade: relógio do servidor atrás do timestamp gravado, ou uma linha com
        // aguardando_desde no futuro. Minuto negativo faria a comparação com o limite inverter.
        var agora = new DateTime(2026, 8, 4, 10, 0, 0);
        Assert.Equal(0, TempoUtil.MinutosUteis(agora.AddHours(1), agora, Comercial, SemFeriado));
        Assert.Equal(0, TempoUtil.MinutosUteis(agora, agora, Comercial, SemFeriado));
    }

    [Fact]
    public void Janela_zerada_nao_trava_e_devolve_zero()
    {
        var janelaRuim = new JanelaAtendimento(8, 20, 0);
        var minutos = TempoUtil.MinutosUteis(
            new DateTime(2026, 1, 1, 8, 0, 0), new DateTime(2026, 12, 31, 20, 0, 0),
            janelaRuim, SemFeriado);

        Assert.Equal(0, minutos);
    }

    [Fact]
    public void Espera_de_mais_de_400_dias_nao_pendura_a_rodada()
    {
        // A trava de 400 iterações limita o valor, mas o método TEM que retornar.
        var minutos = TempoUtil.MinutosUteis(
            new DateTime(2024, 1, 1, 8, 0, 0), new DateTime(2026, 8, 4, 20, 0, 0),
            Comercial, SemFeriado);

        Assert.True(minutos > 0);
    }
}

public class JanelaAtendimentoTests
{
    private static readonly IReadOnlySet<DateOnly> SemFeriado = new HashSet<DateOnly>();

    [Fact]
    public void Padrao_e_8h_as_20h_de_segunda_a_sabado()
    {
        var p = JanelaAtendimento.Padrao;
        Assert.Equal(8, p.HoraInicio);
        Assert.Equal(20, p.HoraFim);
        Assert.Equal(126, p.DiasSemana);
    }

    [Theory]
    [InlineData(7, 59, false)]   // antes de abrir
    [InlineData(8, 0, true)]     // abertura: INCLUSIVA
    [InlineData(19, 59, true)]
    [InlineData(20, 0, false)]   // fechamento: EXCLUSIVO
    [InlineData(23, 0, false)]
    public void Limites_da_hora_sao_inicio_inclusivo_e_fim_exclusivo(int hora, int minuto, bool dentro)
    {
        var terca = new DateTime(2026, 8, 4, hora, minuto, 0);
        Assert.Equal(dentro, JanelaAtendimento.Padrao.Contem(terca, SemFeriado));
    }

    [Fact]
    public void Domingo_e_feriado_estao_fora_mesmo_no_horario_comercial()
    {
        var domingo = new DateTime(2026, 8, 2, 10, 0, 0);
        Assert.False(JanelaAtendimento.Padrao.Contem(domingo, SemFeriado));

        var feriado = new DateTime(2026, 9, 7, 10, 0, 0);   // segunda, Independência
        Assert.True(JanelaAtendimento.Padrao.Contem(feriado, SemFeriado));
        Assert.False(JanelaAtendimento.Padrao.Contem(
            feriado, new HashSet<DateOnly> { DateOnly.FromDateTime(feriado) }));
    }
}
