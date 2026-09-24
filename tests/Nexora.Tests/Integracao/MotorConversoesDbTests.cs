using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core;
using Nexora.Core.Conversoes;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Conversoes;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>A DRENAGEM DA FILA DE CONVERSÕES (INT-4).
///
/// ===================== AS TRÊS COISAS QUE SÓ AQUI SÃO DECIDIDAS =====================
///   • **o expirado sai sem tocar a rede** — a Meta recusa a requisição inteira por causa de um
///     evento de mais de 7 dias, e gastar uma chamada nele é gastar por nada;
///   • **o token morto desliga a credencial** — insistir 3× são três linhas idênticas, e sem a
///     desativação a tela diria "enviando" enquanto nada sai;
///   • **`PodeEnviar` é conferido DE NOVO** — entre enfileirar e drenar passam minutos, e é
///     exatamente nessa janela que alguém desliga o interruptor.
///  ==================================================================================</summary>
[Collection("banco")]
public class MotorConversoesDbTests(BancoTeste banco)
{
    private static readonly DateTimeOffset Marco = new(2026, 3, 20, 12, 0, 0, TimeSpan.Zero);

    private static SalvarCredencial Conectado => new(
        "1234567890123456", "EAAGtokenbemlongoparaMascarar", null, true, true, true, true);

    // ==================================================================== o caminho feliz
    [Fact]
    public async Task O_EVENTO_ACEITO_VIRA_ENTREGUE_COM_FBTRACE()
    {
        var (db, tx, amb) = await PrepararAsync("feliz");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        await amb.Publicador.PublicarLeadAsync(amb.Cenario.Contato, default);
        db.ChangeTracker.Clear();

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Tentadas);
        Assert.Equal(1, r.Entregues);

        db.ChangeTracker.Clear();
        var evento = await db.EventosConversao.IgnoreQueryFilters().AsNoTracking().SingleAsync();

        Assert.Equal(StatusConversao.Entregue, evento.Status);
        Assert.Equal(Marco.UtcDateTime, evento.EntregueEm);
        Assert.Null(evento.ProximaTentativaEm);
        Assert.Null(evento.Erro);
        Assert.Equal(1, (int)evento.Tentativas);

        // O `fbtrace_id` é a primeira coisa que o suporte da Meta pede quando o cliente abre um
        // chamado. Sem ele guardado, a conversa começa com "não temos como rastrear".
        Assert.Equal("fbtrace-ok", evento.FbtraceId);

        // E o que foi mandado: o pixel e o token da credencial DAQUELA empresa.
        var chamada = Assert.Single(amb.Cliente.Chamadas);
        Assert.Equal("1234567890123456", chamada.PixelId);
        Assert.Equal("EAAGtokenbemlongoparaMascarar", chamada.Token);
    }

    // ==================================================================== a janela de 7 dias
    [Fact]
    public async Task O_EVENTO_EXPIRADO_SAI_DA_FILA_SEM_TOCAR_A_REDE()
    {
        // ⚠️ E `expirado`, NÃO `falhou`. A diferença é um botão na tela: reenviar um evento fora da
        // janela nunca vai funcionar, e oferecê-lo seria oferecer um gesto que só pode fracassar.
        var (db, tx, amb) = await PrepararAsync("expirado");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        await amb.Publicador.PublicarLeadAsync(amb.Cenario.Contato, default);
        db.ChangeTracker.Clear();

        // Oito dias depois: o evento passou dos 7 que a Meta aceita.
        amb.Relogio.Avancar(TimeSpan.FromDays(8));

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Expiradas);
        Assert.Equal(0, r.Tentadas);
        Assert.Empty(amb.Cliente.Chamadas);      // nenhuma chamada de rede

        db.ChangeTracker.Clear();
        var evento = await db.EventosConversao.IgnoreQueryFilters().AsNoTracking().SingleAsync();

        Assert.Equal(StatusConversao.Expirado, evento.Status);
        Assert.Null(evento.ProximaTentativaEm);
        Assert.Contains("7 dias", evento.Erro);
        Assert.Equal(0, (int)evento.Tentativas);  // não gastou tentativa nenhuma
    }

    // ==================================================================== o retry
    [Fact]
    public async Task ERRO_TRANSITORIO_REAGENDA_COM_BACKOFF_E_DESISTE_NA_TERCEIRA()
    {
        var (db, tx, amb) = await PrepararAsync("backoff");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        await amb.Publicador.PublicarLeadAsync(amb.Cenario.Contato, default);
        db.ChangeTracker.Clear();

        // Código 2: a Meta com problema. Transitório — esperar resolve.
        amb.Cliente.Resposta = new ResultadoEnvioMeta(false, 500, 2, "t1", "A Meta está instável.");

        var primeira = await amb.Motor.ExecutarAsync();
        Assert.Equal(1, primeira.Reagendadas);

        db.ChangeTracker.Clear();
        var depoisDe1 = await db.EventosConversao.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal(StatusConversao.Pendente, depoisDe1.Status);
        Assert.Equal(Marco.UtcDateTime.AddMinutes(1), depoisDe1.ProximaTentativaEm);
        Assert.Equal(2, depoisDe1.CodigoMeta);

        // Segunda: 5 minutos.
        amb.Relogio.Avancar(TimeSpan.FromMinutes(2));
        await amb.Motor.ExecutarAsync();
        db.ChangeTracker.Clear();
        var depoisDe2 = await db.EventosConversao.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal(2, (int)depoisDe2.Tentativas);
        Assert.Equal(Marco.UtcDateTime.AddMinutes(2 + 5), depoisDe2.ProximaTentativaEm);

        // Terceira: acabou. NÃO volta sozinha.
        amb.Relogio.Avancar(TimeSpan.FromMinutes(10));
        var terceira = await amb.Motor.ExecutarAsync();
        Assert.Equal(1, terceira.Desistidas);

        db.ChangeTracker.Clear();
        var fim = await db.EventosConversao.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal(StatusConversao.Falhou, fim.Status);
        Assert.Null(fim.ProximaTentativaEm);
        Assert.Equal(3, (int)fim.Tentativas);

        // E a credencial NÃO foi desativada: erro transitório não é token morto.
        Assert.Null((await db.CredenciaisConversao.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync()).DesativadaEm);
    }

    // ==================================================================== o token morto
    [Fact]
    public async Task TOKEN_RECUSADO_DESISTE_NA_PRIMEIRA_E_DESLIGA_A_CREDENCIAL_COM_MOTIVO()
    {
        // ⚠️ O ESTADO QUE ISTO EVITA: credencial marcada como ativa pela pessoa, nada saindo, e
        // ninguém sabendo por quê. A frase vai para a TELA e diz o que fazer.
        var (db, tx, amb) = await PrepararAsync("token-morto");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        await amb.Publicador.PublicarLeadAsync(amb.Cenario.Contato, default);
        db.ChangeTracker.Clear();

        amb.Cliente.Resposta = new ResultadoEnvioMeta(
            false, 200, 190, "t9", "A Meta recusou: Invalid OAuth access token.");

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Desistidas);
        Assert.Equal(0, r.Reagendadas);          // uma tentativa só

        db.ChangeTracker.Clear();
        var evento = await db.EventosConversao.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal(StatusConversao.Falhou, evento.Status);
        Assert.Equal(190, evento.CodigoMeta);
        Assert.Equal(1, (int)evento.Tentativas);

        var credencial = await db.CredenciaisConversao.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync();

        Assert.Equal(Marco.UtcDateTime, credencial.DesativadaEm);
        Assert.Contains("Gerenciador de Eventos", credencial.DesativadaMotivo);

        // ⚠️ `Ativo` NÃO foi tocado: é o interruptor da PESSOA. Sobrescrevê-lo faria "religar" virar
        // adivinhação — e o passo de "Primeiros passos" não saberia se deve acender de novo.
        Assert.True(credencial.Ativo);
        Assert.False(credencial.PodeEnviar(TipoConversao.Lead));
    }

    // ==================================================================== o interruptor no meio
    [Fact]
    public async Task DESLIGAR_ENTRE_ENFILEIRAR_E_DRENAR_PARA_O_ENVIO()
    {
        // ⚠️ POR QUE `PodeEnviar` É CONFERIDO DUAS VEZES. Entre o evento entrar na fila e a rodada
        // acontecer passam minutos — e é exatamente nessa janela que alguém retira o consentimento.
        // Sem esta checagem, o dado pessoal sairia depois de a pessoa ter dito para não sair.
        var (db, tx, amb) = await PrepararAsync("desligou-no-meio");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        await amb.Publicador.PublicarLeadAsync(amb.Cenario.Contato, default);
        db.ChangeTracker.Clear();

        // A conversão já está na fila. Agora o dono retira o consentimento — o que cancela as
        // pendentes; para provar a guarda do motor, a linha é devolvida para `pendente` à mão.
        await amb.Conversoes.SalvarAsync(Conectado with { ConsentimentoDeclarado = false }, default);
        db.ChangeTracker.Clear();

        await db.EventosConversao.IgnoreQueryFilters().ExecuteUpdateAsync(s => s
            .SetProperty(e => e.Status, StatusConversao.Pendente)
            .SetProperty(e => e.ProximaTentativaEm, Marco.UtcDateTime));
        db.ChangeTracker.Clear();

        await amb.Motor.ExecutarAsync();

        Assert.Empty(amb.Cliente.Chamadas);      // nada foi para a rede

        db.ChangeTracker.Clear();
        var evento = await db.EventosConversao.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Contains("desligado", evento.Erro);
    }

    // ==================================================================== o teto por rodada
    [Fact]
    public async Task A_RODADA_LEVA_NO_MAXIMO_CINQUENTA()
    {
        // Um cliente que voltou depois de um dia fora tem fila acumulada. Drenar tudo de uma vez
        // seguraria a rodada e atrasaria os eventos novos — e aqui há uma razão extra: a Graph API
        // tem limite de taxa, e martelar 500 chamadas seguidas é o caminho para o código 4.
        var (db, tx, amb) = await PrepararAsync("teto");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        db.ChangeTracker.Clear();

        for (var i = 0; i < 55; i++)
        {
            var contato = new Contato
            {
                EmpresaId = amb.Cenario.Id, Nome = $"Lead {i}",
                Telefone = $"55849700{i:D5}"
            };
            db.Contatos.Add(contato);
            await db.SaveChangesAsync();
            await amb.Publicador.PublicarLeadAsync(contato, default);
        }
        db.ChangeTracker.Clear();

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(PoliticaConversao.MaximoPorRodada, r.Tentadas);
        Assert.Equal(PoliticaConversao.MaximoPorRodada, amb.Cliente.Chamadas.Count);

        // O resto sai na passada seguinte.
        db.ChangeTracker.Clear();
        Assert.Equal(5, await db.EventosConversao.IgnoreQueryFilters()
            .CountAsync(e => e.Status == StatusConversao.Pendente));
    }

    // ==================================================================== isolamento
    [Fact]
    public async Task CADA_EMPRESA_ENVIA_COM_O_PROPRIO_PIXEL_E_TOKEN()
    {
        // ⚠️ O PIOR DEFEITO IMAGINÁVEL DESTE BLOCO: o telefone do cliente de uma empresa saindo pelo
        // pixel de outra. A rodada é uma só para todas, então a credencial tem de ser buscada POR
        // EVENTO, não uma vez.
        var (db, tx, amb) = await PrepararAsync("iso-motor");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        db.ChangeTracker.Clear();

        var outra = await Semeador.TenantAsync(db, "motorconv-iso-b");
        amb.Contexto.EmpresaId = outra.Id;
        amb.Contexto.UsuarioId = outra.Dono.Id;
        await amb.Conversoes.SalvarAsync(
            Conectado with { Identificador = "9999999999", Token = "EAAGoutrotokenbemlongo" }, default);
        db.ChangeTracker.Clear();

        await amb.Publicador.PublicarLeadAsync(amb.Cenario.Contato, default);
        await amb.Publicador.PublicarLeadAsync(
            await db.Contatos.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(c => c.Id == outra.Contato.Id), default);
        db.ChangeTracker.Clear();

        await amb.Motor.ExecutarAsync();

        Assert.Equal(2, amb.Cliente.Chamadas.Count);

        var pixeis = amb.Cliente.Chamadas.Select(c => (c.PixelId, c.Token)).ToHashSet();
        Assert.Contains(("1234567890123456", "EAAGtokenbemlongoparaMascarar"), pixeis);
        Assert.Contains(("9999999999", "EAAGoutrotokenbemlongo"), pixeis);
    }

    // ==================================================================== o expurgo
    [Fact]
    public async Task O_EXPURGO_APAGA_O_REGISTRO_VELHO_E_NAO_O_RASTRO()
    {
        // ⚠️ A ASSIMETRIA É DELIBERADA. O registro do que já foi enviado sai em 30 dias; o RASTRO
        // não tem prazo, porque a venda pode fechar em três meses e o `Purchase` precisa do `fbc` do
        // clique original. Ele morre com a anonimização do contato, não com o calendário.
        var (db, tx, amb) = await PrepararAsync("expurgo");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);

        db.RastreiosLead.Add(new RastreioLead
        {
            EmpresaId = amb.Cenario.Id,
            ContatoId = amb.Cenario.Contato.Id,
            Fonte = FonteRastreio.FormularioSite,
            UtmCampaign = "campanha-velha",
            Identificadores = RegrasRastreio.Montar((RegrasRastreio.ChaveFbc, "fb.1.1.IwAR-velho")),
            OcorridoEm = Marco.UtcDateTime.AddDays(-200)
        });
        await db.SaveChangesAsync();

        await amb.Publicador.PublicarLeadAsync(amb.Cenario.Contato, default);
        await amb.Motor.ExecutarAsync();
        db.ChangeTracker.Clear();

        // Trinta e um dias depois.
        amb.Relogio.Avancar(TimeSpan.FromDays(31));
        Assert.Equal(1, await amb.Motor.ExpurgarAntigosAsync());

        db.ChangeTracker.Clear();
        Assert.Empty(await db.EventosConversao.IgnoreQueryFilters().AsNoTracking().ToListAsync());

        // O rastro FICA — e com o `fbc` intacto.
        var rastro = await db.RastreiosLead.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal("fb.1.1.IwAR-velho",
            RegrasRastreio.Ler(rastro.Identificadores)[RegrasRastreio.ChaveFbc]);
    }

    [Fact]
    public async Task O_EXPURGO_NAO_TOCA_NO_QUE_AINDA_ESTA_DENTRO_DO_PRAZO()
    {
        var (db, tx, amb) = await PrepararAsync("expurgo-recente");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        await amb.Publicador.PublicarLeadAsync(amb.Cenario.Contato, default);
        db.ChangeTracker.Clear();

        amb.Relogio.Avancar(TimeSpan.FromDays(29));
        Assert.Equal(0, await amb.Motor.ExpurgarAntigosAsync());
    }

    // ==================================================================== o botão de teste
    [Fact]
    public async Task O_BOTAO_DE_TESTE_MANDA_DADO_SINTETICO_E_NAO_GRAVA_NADA_NA_FILA()
    {
        // ⚠️ AS DUAS COISAS SÃO A MESMA DECISÃO. Um contato real faria o botão enviar uma conversão
        // de verdade — e o `Lead` daquela pessoa sairia duas vezes no dia em que ela virasse lead
        // mesmo, porque o único parcial já estaria ocupado.
        var (db, tx, amb) = await PrepararAsync("teste");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado with { CodigoTeste = "TEST123" }, default);
        db.ChangeTracker.Clear();

        var r = await amb.Conversoes.TestarAsync(default);

        Assert.True(r.Ok);
        Assert.Equal(200, r.Codigo);
        Assert.Equal("fbtrace-ok", r.FbtraceId);

        db.ChangeTracker.Clear();
        Assert.Empty(await db.EventosConversao.IgnoreQueryFilters().AsNoTracking().ToListAsync());

        var chamada = Assert.Single(amb.Cliente.Chamadas);

        // O código de teste vai: é ele que põe o evento em "Eventos de teste" e o mantém FORA da
        // otimização das campanhas do cliente. Teste que polui o pixel é pior que teste nenhum.
        Assert.Equal("TEST123", chamada.CodigoTeste);

        // E nenhum telefone de cliente foi hasheado para dentro dele.
        var corpo = JsonNode.Parse(chamada.Corpo)!["data"]![0]!;
        Assert.Equal(HashPessoal.Telefone("5500000000000"),
            (string)corpo["user_data"]!["ph"]![0]!);
    }

    [Fact]
    public async Task O_TESTE_QUE_FALHA_POR_TOKEN_DESLIGA_A_CREDENCIAL_NA_HORA()
    {
        // É o ponto do botão: descobrir AGORA, olhando a tela, em vez de semanas depois ao perceber
        // que nada saiu.
        var (db, tx, amb) = await PrepararAsync("teste-ruim");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        db.ChangeTracker.Clear();

        amb.Cliente.Resposta = new ResultadoEnvioMeta(false, 200, 190, "t1", "Invalid OAuth token.");

        var r = await amb.Conversoes.TestarAsync(default);

        Assert.False(r.Ok);
        db.ChangeTracker.Clear();
        var credencial = await db.CredenciaisConversao.AsNoTracking().SingleAsync();
        Assert.NotNull(credencial.DesativadaEm);
        Assert.Contains("token", credencial.DesativadaMotivo);
    }

    [Fact]
    public async Task UM_TESTE_QUE_FUNCIONA_RELIGA_O_QUE_O_MOTOR_DESLIGOU()
    {
        // O par do gesto acima: trocar o token e testar é como se diz ao sistema que resolveu.
        var (db, tx, amb) = await PrepararAsync("teste-religa");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        db.ChangeTracker.Clear();

        await db.CredenciaisConversao.ExecuteUpdateAsync(s => s
            .SetProperty(c => c.DesativadaEm, Marco.UtcDateTime)
            .SetProperty(c => c.DesativadaMotivo, "A Meta recusou o token."));
        db.ChangeTracker.Clear();

        Assert.True((await amb.Conversoes.TestarAsync(default)).Ok);

        db.ChangeTracker.Clear();
        var credencial = await db.CredenciaisConversao.AsNoTracking().SingleAsync();
        Assert.Null(credencial.DesativadaEm);
        Assert.Null(credencial.DesativadaMotivo);
    }

    [Fact]
    public async Task SEM_TOKEN_O_BOTAO_DE_TESTE_RECUSA_COM_FRASE()
    {
        var (db, tx, amb) = await PrepararAsync("teste-sem-token");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado with { Token = null }, default);
        db.ChangeTracker.Clear();

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Conversoes.TestarAsync(default));

        Assert.Contains("token", erro.Message);
        Assert.Empty(amb.Cliente.Chamadas);
    }

    // ==================================================================== o reenvio
    [Fact]
    public async Task REENVIAR_VOLTA_A_LINHA_PARA_A_FILA_COM_AS_TENTATIVAS_ZERADAS()
    {
        var (db, tx, amb) = await PrepararAsync("reenvio");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        await amb.Publicador.PublicarLeadAsync(amb.Cenario.Contato, default);
        db.ChangeTracker.Clear();

        amb.Cliente.Resposta = new ResultadoEnvioMeta(false, 200, 190, "t1", "token ruim");
        await amb.Motor.ExecutarAsync();
        db.ChangeTracker.Clear();

        var id = (await db.EventosConversao.IgnoreQueryFilters().AsNoTracking().SingleAsync()).Id;

        // ⚠️ O GESTO REAL É EM DUAS PARTES, e um teste mostrou isso: o `190` desativou a
        // credencial, então reenviar ANTES de trocar o token devolveria a linha para a fila só para
        // ela falhar de novo. Primeiro o token novo (o que religa), depois o reenvio.
        var antes = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Conversoes.ReenviarAsync(id, default));
        Assert.Contains("desligado", antes.Message);
        db.ChangeTracker.Clear();

        await amb.Conversoes.SalvarAsync(Conectado with { Token = "EAAGtokennovobemlongo" }, default);
        db.ChangeTracker.Clear();

        amb.Cliente.Resposta = new ResultadoEnvioMeta(true, 200, null, "t2", null);
        await amb.Conversoes.ReenviarAsync(id, default);
        db.ChangeTracker.Clear();

        var voltou = await db.EventosConversao.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal(StatusConversao.Pendente, voltou.Status);
        Assert.Equal(0, (int)voltou.Tentativas);
        Assert.Null(voltou.Erro);
        Assert.Null(voltou.CodigoMeta);

        // E a próxima rodada entrega.
        await amb.Motor.ExecutarAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(StatusConversao.Entregue,
            (await db.EventosConversao.IgnoreQueryFilters().AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task REENVIAR_FORA_DA_JANELA_DE_SETE_DIAS_E_RECUSADO_COM_A_RAZAO()
    {
        // ⚠️ A JANELA É CONFERIDA AQUI TAMBÉM. Entre a falha e o clique podem passar dias, e um
        // reenvio fora dos 7 dias seria uma requisição que a Meta recusa inteira — com o agravante
        // de o dono achar que resolveu.
        var (db, tx, amb) = await PrepararAsync("reenvio-velho");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        await amb.Publicador.PublicarLeadAsync(amb.Cenario.Contato, default);
        db.ChangeTracker.Clear();

        amb.Cliente.Resposta = new ResultadoEnvioMeta(false, 200, 190, "t1", "token ruim");
        await amb.Motor.ExecutarAsync();
        db.ChangeTracker.Clear();

        var id = (await db.EventosConversao.IgnoreQueryFilters().AsNoTracking().SingleAsync()).Id;

        amb.Relogio.Avancar(TimeSpan.FromDays(8));

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Conversoes.ReenviarAsync(id, default));

        Assert.Contains("7 dias", erro.Message);
        Assert.True(erro.Conflito);
    }

    [Fact]
    public async Task SO_O_QUE_FALHOU_PODE_SER_REENVIADO()
    {
        // `pendente` já vai ser tentado sozinho, e `entregue` mandaria o mesmo evento duas vezes
        // para quem já processou.
        var (db, tx, amb) = await PrepararAsync("reenvio-entregue");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        await amb.Publicador.PublicarLeadAsync(amb.Cenario.Contato, default);
        db.ChangeTracker.Clear();

        await amb.Motor.ExecutarAsync();
        db.ChangeTracker.Clear();

        var entregue = await db.EventosConversao.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.Equal(StatusConversao.Entregue, entregue.Status);

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => amb.Conversoes.ReenviarAsync(entregue.Id, default));
        Assert.Contains("falhou", erro.Message);

        // E a TELA não oferece o botão para ele.
        var painel = await amb.Conversoes.ObterAsync(default);
        Assert.False(Assert.Single(painel.Conversoes).PodeReenviar);
    }

    [Fact]
    public async Task A_TELA_NAO_OFERECE_REENVIO_PARA_O_QUE_EXPIROU()
    {
        // ⚠️ É A RAZÃO DE `expirado` SER UM STATUS PRÓPRIO. Como `falhou`, a tela ofereceria um botão
        // que só pode fracassar — e o dono clicaria nele, veria falhar, e clicaria de novo.
        var (db, tx, amb) = await PrepararAsync("tela-expirado");
        using var _ = db; using var __ = tx;

        await amb.Conversoes.SalvarAsync(Conectado, default);
        await amb.Publicador.PublicarLeadAsync(amb.Cenario.Contato, default);
        db.ChangeTracker.Clear();

        amb.Relogio.Avancar(TimeSpan.FromDays(8));
        await amb.Motor.ExecutarAsync();
        db.ChangeTracker.Clear();

        var conversao = Assert.Single((await amb.Conversoes.ObterAsync(default)).Conversoes);

        Assert.Equal("expirado", conversao.Status);
        Assert.False(conversao.PodeReenviar);

        // E o payload aparece na tela — sem o token dentro, porque ele nunca fez parte do corpo
        // guardado.
        Assert.DoesNotContain("access_token", conversao.Payload);
        Assert.DoesNotContain("EAAG", conversao.Payload);
    }

    // ==================================================================== apoio
    private sealed record Ambiente(
        Cenario Cenario, ContextoMutavel Contexto, RelogioFalso Relogio,
        ClienteMetaFalso Cliente, MotorConversoes Motor,
        IPublicadorConversoes Publicador, IServicoConversoes Conversoes);

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo)
    {
        var ctx = new ContextoMutavel();
        var relogio = new RelogioFalso(Marco);
        var db = banco.NovoContexto(ctx, relogio);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"motorconv-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        var cliente = new ClienteMetaFalso();

        return (db, tx, new Ambiente(
            cenario, ctx, relogio, cliente,
            new MotorConversoes(db, cliente, relogio, NullLogger<MotorConversoes>.Instance),
            new PublicadorConversoes(db, relogio, NullLogger<PublicadorConversoes>.Instance),
            new ServicoConversoes(db, ctx, cliente, relogio)));
    }
}
