using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Api.Controllers;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>O vocabulário de etiquetas.
///
/// A regra que sustenta o resto é uma só: o nome é único por empresa, SEM diferenciar maiúscula
/// de minúscula. Ela é o que separa uma lista em que o dono confia de uma em que "Revendedor",
/// "revendedor" e "REVENDEDOR" convivem — e, quando o filtro da issue #3 chegar, é o que separa
/// uma busca que acha tudo de uma que acha um terço.
///
/// Ela é garantida em DOIS lugares de propósito, e há um teste para cada: o serviço, que devolve
/// mensagem legível, e o índice funcional `uq_etiquetas_nome`, que é quem de fato fecha a janela
/// entre a consulta e o `SaveChanges`.</summary>
[Collection("banco")]
public class EtiquetasDbTests(BancoTeste banco)
{
    // ==================================================================== papel
    [Fact]
    public void VENDEDOR_LE_A_LISTA_MAS_NAO_ESCREVE()
    {
        // ===================== ONDE ESTA REGRA VIVE =====================
        // O enforcement é o [Authorize(Roles="dono")] no controller, não no serviço. Testar sem
        // subir HTTP significa ler o ATRIBUTO — que é exatamente o artefato que decide.
        //
        // E a assimetria é o ponto deste bloco: criar etiqueta é configuração e define o
        // vocabulário da empresa; APLICAR é trabalho do dia, e para escolher qual aplicar o
        // vendedor precisa da lista. Fechar o GET tornaria a etiqueta inútil para quem a usa.
        // ===============================================================
        var tipo = typeof(EtiquetasController);

        foreach (var metodo in new[] { nameof(EtiquetasController.Criar),
                                       nameof(EtiquetasController.Atualizar),
                                       nameof(EtiquetasController.Remover) })
        {
            var atributo = tipo.GetMethod(metodo)!
                .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
                .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
                .FirstOrDefault();

            Assert.NotNull(atributo);
            Assert.Equal("dono", atributo!.Roles);
        }

        // O GET não restringe papel — e a ausência é deliberada, não esquecimento.
        var get = tipo.GetMethod(nameof(EtiquetasController.Listar))!
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>();
        Assert.DoesNotContain(get, a => a.Roles == "dono");
    }

    // ==================================================================== nome único
    [Fact]
    public async Task NOME_REPETIDO_EM_OUTRA_CAIXA_E_RECUSADO()
    {
        var (db, tx, s, _) = await PrepararAsync("nome-caixa");
        using var _1 = db; using var _2 = tx;

        await s.CriarAsync(new NovaEtiqueta("Revendedor", null), default);

        // "revendedor" e "Revendedor" são a MESMA etiqueta para quem lê a lista. Deixar as duas
        // entrarem é criar dois rótulos que parecem um só.
        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.CriarAsync(new NovaEtiqueta("revendedor", null), default));

        Assert.Contains("Já existe", erro.Message);
    }

    [Fact]
    public async Task RENOMEAR_MANTENDO_O_PROPRIO_NOME_E_PERMITIDO()
    {
        // O `ignorarId` existe para isto: trocar só a cor não pode esbarrar no próprio nome.
        var (db, tx, s, _) = await PrepararAsync("nome-proprio");
        using var _1 = db; using var _2 = tx;

        var id = await s.CriarAsync(new NovaEtiqueta("Urgente", "#B4552F"), default);
        await s.AtualizarAsync(id, new EditarEtiqueta("Urgente", "#2E7A56"), default);

        var etiqueta = await db.Etiquetas.AsNoTracking().SingleAsync(e => e.Id == id);
        Assert.Equal("Urgente", etiqueta.Nome);
        Assert.Equal("#2E7A56", etiqueta.Cor);
    }

    /// <summary>⚠️ ESTE É O TESTE QUE IMPORTA DOS DOIS. O serviço checa o nome em memória, e entre
    /// a consulta dele e o `SaveChanges` cabe outra requisição criando o mesmo nome. Quem fecha
    /// essa janela é o índice — e é ele que este teste exercita, passando POR CIMA do serviço.
    ///
    /// Se algum dia o índice sumir, o teste do serviço acima continuaria verde e a duplicata
    /// entraria em produção sob concorrência.</summary>
    [Fact]
    public async Task QUEM_GARANTE_O_NOME_UNICO_E_O_BANCO_NAO_O_SERVICO()
    {
        var (db, tx, _, cenario) = await PrepararAsync("nome-banco");
        using var _1 = db; using var _2 = tx;

        db.Etiquetas.Add(new Etiqueta { EmpresaId = cenario.Id, Nome = "VIP" });
        await db.SaveChangesAsync();

        db.Etiquetas.Add(new Etiqueta { EmpresaId = cenario.Id, Nome = "vip" });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // ==================================================================== tenant
    [Fact]
    public async Task O_MESMO_NOME_EM_OUTRA_EMPRESA_E_ACEITO()
    {
        // O índice é (empresa_id, lower(nome)). Duas padarias podem ter "Revendedor" — o
        // vocabulário é de cada uma.
        var (db, tx, s, primeira) = await PrepararAsync("tenant-a");
        using var _1 = db; using var _2 = tx;

        await s.CriarAsync(new NovaEtiqueta("Revendedor", null), default);

        var outra = await Semeador.TenantAsync(db, "etiquetas-tenant-b");
        db.Etiquetas.Add(new Etiqueta { EmpresaId = outra.Id, Nome = "Revendedor" });

        // Não estoura: o índice é por empresa.
        await db.SaveChangesAsync();

        var quantas = await db.Etiquetas.IgnoreQueryFilters()
            .CountAsync(e => e.Nome == "Revendedor"
                          && (e.EmpresaId == primeira.Id || e.EmpresaId == outra.Id));
        Assert.Equal(2, quantas);
    }

    [Fact]
    public async Task A_EMPRESA_NAO_ENXERGA_ETIQUETA_DE_OUTRA()
    {
        var (db, tx, s, _) = await PrepararAsync("tenant-isolado");
        using var _1 = db; using var _2 = tx;

        var outra = await Semeador.TenantAsync(db, "etiquetas-vizinha");
        db.Etiquetas.Add(new Etiqueta { EmpresaId = outra.Id, Nome = "Da vizinha" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var lista = await s.ListarAsync(null, OrdemEtiqueta.Nome, default);
        Assert.DoesNotContain(lista, e => e.Nome == "Da vizinha");
    }

    // ==================================================================== validação
    [Theory]
    [InlineData(null, "#2F5D3A")]        // vazia vira o padrão
    [InlineData("", "#2F5D3A")]
    [InlineData("#b4552f", "#B4552F")]   // normaliza para maiúscula
    public async Task A_COR_VAZIA_VIRA_O_PADRAO_E_A_VALIDA_E_NORMALIZADA(string? informada, string esperada)
    {
        var (db, tx, s, _) = await PrepararAsync($"cor-{informada?.Length ?? 9}");
        using var _1 = db; using var _2 = tx;

        var id = await s.CriarAsync(new NovaEtiqueta("Teste", informada), default);

        var etiqueta = await db.Etiquetas.AsNoTracking().SingleAsync(e => e.Id == id);
        Assert.Equal(esperada, etiqueta.Cor);
    }

    [Theory]
    [InlineData("vermelho")]
    [InlineData("#GGG")]
    [InlineData("#12345")]
    [InlineData("red; background: url(x)")]
    public async Task COR_QUE_NAO_E_HEXADECIMAL_E_RECUSADA(string cor)
    {
        // A cor vai direto para o `style` do chip. Texto livre aqui é o dono escrevendo CSS na
        // tela de todo mundo da empresa dele.
        var (db, tx, s, _) = await PrepararAsync($"cor-ruim-{cor.Length}");
        using var _1 = db; using var _2 = tx;

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.CriarAsync(new NovaEtiqueta("Teste", cor), default));
    }

    [Fact]
    public async Task NOME_CURTO_DEMAIS_E_RECUSADO()
    {
        var (db, tx, s, _) = await PrepararAsync("nome-curto");
        using var _1 = db; using var _2 = tx;

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.CriarAsync(new NovaEtiqueta(" a ", null), default));
    }

    // ==================================================================== lista e remoção
    [Fact]
    public async Task A_LISTA_VEM_ORDENADA_POR_NOME()
    {
        // Não há ordem manual: etiqueta não é etapa de funil e não tem sequência. Quem procura
        // "Urgente" numa lista procura em ordem alfabética.
        var (db, tx, s, _) = await PrepararAsync("ordem");
        using var _1 = db; using var _2 = tx;

        foreach (var nome in new[] { "Urgente", "Antigo", "Revendedor" })
            await s.CriarAsync(new NovaEtiqueta(nome, null), default);

        var lista = await s.ListarAsync(null, OrdemEtiqueta.Nome, default);
        Assert.Equal(new[] { "Antigo", "Revendedor", "Urgente" }, lista.Select(e => e.Nome));
    }

    [Fact]
    public async Task APAGAR_NAO_DEIXA_RESTO()
    {
        var (db, tx, s, _) = await PrepararAsync("apagar");
        using var _1 = db; using var _2 = tx;

        var id = await s.CriarAsync(new NovaEtiqueta("Descartável", null), default);
        await s.RemoverAsync(id, default);
        db.ChangeTracker.Clear();

        Assert.False(await db.Etiquetas.AnyAsync(e => e.Id == id));

        // E o nome fica livre de novo — apagar não pode deixar o índice segurando um fantasma.
        await s.CriarAsync(new NovaEtiqueta("Descartável", null), default);
    }

    [Fact]
    public async Task APAGAR_O_QUE_NAO_EXISTE_DA_MENSAGEM_LEGIVEL()
    {
        var (db, tx, s, _) = await PrepararAsync("apagar-fantasma");
        using var _1 = db; using var _2 = tx;

        await Assert.ThrowsAsync<RegraDeNegocioException>(() => s.RemoverAsync(999_999, default));
    }

    // ==================================================================== status HTTP
    /// <summary>===================== O STATUS FAZ PARTE DA REGRA =====================
    /// Quatro recusas diferentes saiam todas como 400 ate o DES-XX, e o cliente so distinguia
    /// lendo a mensagem em portugues. Agora cada uma diz O QUE aconteceu no proprio status:
    ///
    ///   400  a ENTRADA esta errada       — nome curto, nome longo, cor invalida
    ///   409  o ESTADO impede             — ja existe outra etiqueta com este nome
    ///   422  um TETO impede              — a empresa chegou nas 60
    ///
    /// A tela mostra `{ erro }` e ignora o numero, mas quem integra com a API nao tem a mensagem:
    /// tem o status. E "tente outro nome" (409) e "apague alguma antes" (422) pedem acoes opostas
    /// de quem automatiza.
    ///
    /// O teste le a EXCECAO, nao a resposta HTTP, porque e a excecao que carrega a decisao — o
    /// `FiltroRegraDeNegocio` so a traduz.
    /// ==========================================================================</summary>
    [Fact]
    public async Task NOME_REPETIDO_E_CONFLITO_409_E_NAO_ENTRADA_INVALIDA_400()
    {
        var (db, tx, s, _) = await PrepararAsync("status-409");
        using var _1 = db; using var _2 = tx;

        await s.CriarAsync(new NovaEtiqueta("Revendedor", null), default);

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.CriarAsync(new NovaEtiqueta("revendedor", null), default));

        Assert.True(erro.Conflito);
        Assert.Null(erro.StatusHttp);   // 409 vem do `Conflito`, sem precisar do status explicito
    }

    [Fact]
    public async Task O_TETO_DE_60_E_422_PORQUE_NAO_E_NEM_ENTRADA_NEM_CONFLITO()
    {
        var (db, tx, s, cenario) = await PrepararAsync("status-422");
        using var _1 = db; using var _2 = tx;

        // Insere as 60 direto, sem passar pelo servico: 60 chamadas de `CriarAsync` fariam 60
        // consultas de nome livre so para chegar no caso que interessa.
        for (var i = 0; i < ServicoEtiquetas.MaximoEtiquetas; i++)
            db.Etiquetas.Add(new Etiqueta { EmpresaId = cenario.Id, Nome = $"Etiqueta {i:D2}" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.CriarAsync(new NovaEtiqueta("A sexagesima primeira", null), default));

        Assert.Equal(422, erro.StatusHttp);
        Assert.Contains("60", erro.Message);
    }

    [Fact]
    public async Task ENTRADA_INVALIDA_CONTINUA_SENDO_400()
    {
        var (db, tx, s, _) = await PrepararAsync("status-400");
        using var _1 = db; using var _2 = tx;

        foreach (var ruim in new[] { new NovaEtiqueta("a", null),
                                     new NovaEtiqueta("Teste", "vermelho") })
        {
            var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
                () => s.CriarAsync(ruim, default));

            Assert.False(erro.Conflito);
            Assert.Null(erro.StatusHttp);
        }
    }

    // ==================================================================== nome longo
    /// <summary>⚠️ ESTE TESTE PROVA UMA MUDANCA DE COMPORTAMENTO, nao um comportamento novo.
    ///
    /// Ate o DES-XX o servico CORTAVA em 30 e devolvia sucesso. A tela nunca reproduzia isso
    /// (`maxlength` de 30 no campo), entao o unico jeito de descobrir era pela API — que e
    /// justamente por onde o seletor de etiquetas e qualquer integracao vao entrar.
    ///
    /// Devolver sucesso depois de alterar o dado do usuario e a pior das tres opcoes: recusar ele
    /// conserta na hora, truncar ele descobre semanas depois olhando a lista.</summary>
    [Fact]
    public async Task NOME_LONGO_DEMAIS_E_RECUSADO_E_NAO_CORTADO_EM_SILENCIO()
    {
        var (db, tx, s, _) = await PrepararAsync("nome-longo");
        using var _1 = db; using var _2 = tx;

        var longo = new string('a', 31);

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.CriarAsync(new NovaEtiqueta(longo, null), default));

        Assert.Contains("30", erro.Message);

        // E o mais importante: nao sobrou nada gravado.
        Assert.False(await db.Etiquetas.AnyAsync(e => e.Nome.StartsWith("aaa")));
    }

    [Fact]
    public async Task EXATAMENTE_30_CARACTERES_PASSA()
    {
        // O limite e inclusivo. Um teste so do lado que recusa deixaria passar um `>=` no lugar
        // do `>`, e ninguem notaria ate um nome de 30 letras ser rejeitado em producao.
        var (db, tx, s, _) = await PrepararAsync("nome-30");
        using var _1 = db; using var _2 = tx;

        var id = await s.CriarAsync(new NovaEtiqueta(new string('a', 30), null), default);

        var etiqueta = await db.Etiquetas.AsNoTracking().SingleAsync(e => e.Id == id);
        Assert.Equal(30, etiqueta.Nome.Length);
    }

    // ==================================================================== busca
    [Fact]
    public async Task A_BUSCA_ACHA_POR_TRECHO_E_SEM_OLHAR_MAIUSCULA()
    {
        var (db, tx, s, _) = await PrepararAsync("busca");
        using var _1 = db; using var _2 = tx;

        foreach (var nome in new[] { "Revendedor", "Urgente", "Pos-venda" })
            await s.CriarAsync(new NovaEtiqueta(nome, null), default);

        // Trecho do MEIO, nao prefixo: quem procura "vend" quer achar "Revendedor" tambem.
        var meio = await s.ListarAsync("vend", OrdemEtiqueta.Nome, default);
        Assert.Equal(new[] { "Pos-venda", "Revendedor" }, meio.Select(e => e.Nome));

        // ⚠️ SEM DIFERENCIAR MAIUSCULA, e isso e obrigatorio, nao gentileza: `uq_etiquetas_nome`
        // impede "VIP" e "vip" coexistirem, entao procurar em minuscula PRECISA achar a que
        // sobreviveu. Uma busca sensivel a caixa mentiria sobre a propria lista.
        var caixa = await s.ListarAsync("URGENTE", OrdemEtiqueta.Nome, default);
        Assert.Equal("Urgente", Assert.Single(caixa).Nome);
    }

    [Fact]
    public async Task BUSCA_VAZIA_OU_SO_ESPACO_NAO_FILTRA_NADA()
    {
        var (db, tx, s, _) = await PrepararAsync("busca-vazia");
        using var _1 = db; using var _2 = tx;

        await s.CriarAsync(new NovaEtiqueta("Revendedor", null), default);

        foreach (var nada in new[] { null, "", "   " })
            Assert.Single(await s.ListarAsync(nada, OrdemEtiqueta.Nome, default));
    }

    [Fact]
    public async Task A_BUSCA_NAO_ATRAVESSA_A_EMPRESA()
    {
        // O filtro de tenant e o `HasQueryFilter`, e a busca so acrescenta um `Where`. Este teste
        // existe para o dia em que alguem "otimizar" a listagem com SQL cru e perder o filtro.
        var (db, tx, s, _) = await PrepararAsync("busca-tenant");
        using var _1 = db; using var _2 = tx;

        var outra = await Semeador.TenantAsync(db, "etiquetas-busca-vizinha");
        db.Etiquetas.Add(new Etiqueta { EmpresaId = outra.Id, Nome = "Revendedor" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Empty(await s.ListarAsync("revend", OrdemEtiqueta.Nome, default));
    }

    // ==================================================================== ordem
    [Fact]
    public async Task RECENTES_DEVOLVE_DA_MAIS_NOVA_PARA_A_MAIS_ANTIGA()
    {
        var (db, tx, s, _) = await PrepararAsync("ordem-recentes");
        using var _1 = db; using var _2 = tx;

        foreach (var nome in new[] { "Primeira", "Segunda", "Terceira" })
            await s.CriarAsync(new NovaEtiqueta(nome, null), default);

        var lista = await s.ListarAsync(null, OrdemEtiqueta.Recentes, default);

        // ⚠️ O QUE ESTE TESTE DE FATO PROTEGE E O DESEMPATE POR `Id`. As tres sao criadas dentro
        // da mesma transacao, e o `InterceptorAuditoria` carimba um instante so por `SaveChanges`
        // — os `criado_em` podem sair IGUAIS. Sem `ThenByDescending(Id)` a ordem seria a que o
        // Postgres escolher, e este teste passaria ou nao conforme o dia.
        Assert.Equal(new[] { "Terceira", "Segunda", "Primeira" }, lista.Select(e => e.Nome));
    }

    [Fact]
    public async Task BUSCA_E_ORDEM_FUNCIONAM_JUNTAS()
    {
        var (db, tx, s, _) = await PrepararAsync("busca-ordem");
        using var _1 = db; using var _2 = tx;

        foreach (var nome in new[] { "Venda direta", "Venda online", "Urgente" })
            await s.CriarAsync(new NovaEtiqueta(nome, null), default);

        var lista = await s.ListarAsync("venda", OrdemEtiqueta.Recentes, default);
        Assert.Equal(new[] { "Venda online", "Venda direta" }, lista.Select(e => e.Nome));
    }

    // ==================================================================== criado_por
    [Fact]
    public async Task QUEM_CRIOU_FICA_GRAVADO()
    {
        var (db, tx, s, cenario) = await PrepararAsync("criador");
        using var _1 = db; using var _2 = tx;

        var id = await s.CriarAsync(new NovaEtiqueta("Revendedor", null), default);

        var etiqueta = await db.Etiquetas.AsNoTracking().SingleAsync(e => e.Id == id);
        Assert.Equal(cenario.Dono.Id, etiqueta.CriadoPor);
    }

    /// <summary>⚠️ `UsuarioId == 0` NAO E O USUARIO ZERO — e "nao ha sessao". Acontece na semente
    /// de desenvolvimento, em migracao e em script. Gravar 0 criaria FK apontando para um usuario
    /// que nao existe, e o INSERT estouraria num lugar sem nada a ver com a causa.
    ///
    /// O idioma `contexto.UsuarioId == 0 ? null : contexto.UsuarioId` e o mesmo de
    /// `ServicoLembretes`, `ServicoVendas` e `ServicoConversas`. Este teste e o que o segura.</summary>
    [Fact]
    public async Task SEM_SESSAO_O_CRIADOR_FICA_NULO_EM_VEZ_DE_ZERO()
    {
        var ctx = new ContextoMutavel();
        using var db = banco.NovoContexto(ctx);
        using var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, "etiquetas-sem-sessao");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = 0;              // <- o ponto do teste
        ctx.Papel = "dono";

        var s = new ServicoEtiquetas(db, ctx);
        var id = await s.CriarAsync(new NovaEtiqueta("Da semente", null), default);

        var etiqueta = await db.Etiquetas.AsNoTracking().SingleAsync(e => e.Id == id);
        Assert.Null(etiqueta.CriadoPor);
    }

    // ==================================================================== aplicar
    [Fact]
    public async Task APLICAR_SUBSTITUI_O_CONJUNTO_INTEIRO()
    {
        var (db, tx, s, c) = await PrepararAsync("aplicar");
        using var _1 = db; using var _2 = tx;

        var a = await s.CriarAsync(new NovaEtiqueta("Revendedor", null), default);
        var b = await s.CriarAsync(new NovaEtiqueta("VIP", null), default);
        var d = await s.CriarAsync(new NovaEtiqueta("Urgente", null), default);

        await s.AplicarAsync(c.Contato.Id, [a, b], default);
        db.ChangeTracker.Clear();
        Assert.Equal(["Revendedor", "VIP"],
            (await s.DoContatoAsync(c.Contato.Id, default)).Select(e => e.Nome));

        // A segunda chamada é o estado FINAL, não um acréscimo: "VIP" sai, "Urgente" entra.
        await s.AplicarAsync(c.Contato.Id, [a, d], default);
        db.ChangeTracker.Clear();
        Assert.Equal(["Revendedor", "Urgente"],
            (await s.DoContatoAsync(c.Contato.Id, default)).Select(e => e.Nome));
    }

    /// <summary>⚠️ A RAZÃO DE SER UM `PUT` QUE SUBSTITUI. Repetir a mesma chamada não pode mudar
    /// nada — é o que torna seguro o duplo clique e o retry de rede. Com POST/DELETE por etiqueta,
    /// "remove" repetido erraria.</summary>
    [Fact]
    public async Task APLICAR_DUAS_VEZES_O_MESMO_CONJUNTO_NAO_MUDA_NADA()
    {
        var (db, tx, s, c) = await PrepararAsync("idempotente");
        using var _1 = db; using var _2 = tx;

        var a = await s.CriarAsync(new NovaEtiqueta("Revendedor", null), default);

        await s.AplicarAsync(c.Contato.Id, [a], default);
        db.ChangeTracker.Clear();
        await s.AplicarAsync(c.Contato.Id, [a], default);
        db.ChangeTracker.Clear();

        Assert.Single(await s.DoContatoAsync(c.Contato.Id, default));
    }

    /// <summary>⚠️ O QUE O "SÓ O DELTA" PROTEGE. Apagar tudo e reinserir seria mais curto de
    /// escrever e perderia `criado_em` de quem já estava lá — "desde quando este cliente é VIP?"
    /// deixaria de ter resposta toda vez que alguém mexesse em OUTRA etiqueta do mesmo contato.</summary>
    [Fact]
    public async Task MEXER_NUMA_ETIQUETA_NAO_REESCREVE_A_DATA_DAS_OUTRAS()
    {
        var (db, tx, s, c) = await PrepararAsync("delta");
        using var _1 = db; using var _2 = tx;

        var antiga = await s.CriarAsync(new NovaEtiqueta("Revendedor", null), default);
        var nova = await s.CriarAsync(new NovaEtiqueta("Urgente", null), default);

        await s.AplicarAsync(c.Contato.Id, [antiga], default);
        db.ChangeTracker.Clear();

        var carimbo = await db.ContatosEtiquetas.AsNoTracking()
            .Where(x => x.EtiquetaId == antiga).Select(x => x.CriadoEm).SingleAsync();

        await s.AplicarAsync(c.Contato.Id, [antiga, nova], default);
        db.ChangeTracker.Clear();

        var depois = await db.ContatosEtiquetas.AsNoTracking()
            .Where(x => x.EtiquetaId == antiga).Select(x => x.CriadoEm).SingleAsync();

        Assert.Equal(carimbo, depois);
    }

    [Fact]
    public async Task APLICAR_LISTA_VAZIA_DESMARCA_TUDO()
    {
        // Desmarcar a última é caso legítimo, e recusá-lo obrigaria a tela a ter um segundo
        // caminho só para isso.
        var (db, tx, s, c) = await PrepararAsync("limpar");
        using var _1 = db; using var _2 = tx;

        var a = await s.CriarAsync(new NovaEtiqueta("Revendedor", null), default);
        await s.AplicarAsync(c.Contato.Id, [a], default);
        db.ChangeTracker.Clear();

        await s.AplicarAsync(c.Contato.Id, [], default);
        db.ChangeTracker.Clear();

        Assert.Empty(await s.DoContatoAsync(c.Contato.Id, default));
    }

    [Fact]
    public async Task ETIQUETA_REPETIDA_NA_MESMA_CHAMADA_NAO_E_ERRO()
    {
        // Não é erro do usuário — é a tela mandando o que tinha na mão. Deduplicar é mais gentil
        // que recusar, e o resultado é o mesmo.
        var (db, tx, s, c) = await PrepararAsync("repetida");
        using var _1 = db; using var _2 = tx;

        var a = await s.CriarAsync(new NovaEtiqueta("Revendedor", null), default);
        await s.AplicarAsync(c.Contato.Id, [a, a, a], default);
        db.ChangeTracker.Clear();

        Assert.Single(await s.DoContatoAsync(c.Contato.Id, default));
    }

    [Fact]
    public async Task ACIMA_DO_TETO_POR_CONTATO_E_RECUSADO()
    {
        var (db, tx, s, c) = await PrepararAsync("teto-contato");
        using var _1 = db; using var _2 = tx;

        var ids = new List<long>();
        for (var i = 0; i <= ServicoEtiquetas.MaximoPorContato; i++)
            ids.Add(await s.CriarAsync(new NovaEtiqueta($"Etiqueta {i}", null), default));

        var erro = await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.AplicarAsync(c.Contato.Id, ids, default));

        Assert.Contains($"{ServicoEtiquetas.MaximoPorContato}", erro.Message);
    }

    /// <summary>A FK composta já recusaria — mas com violação crua de banco, que vira 500. O
    /// serviço checa antes para a mensagem ser legível. Mesma divisão do nome único: o serviço
    /// explica, o banco garante.</summary>
    [Fact]
    public async Task ETIQUETA_DE_OUTRA_EMPRESA_DA_MENSAGEM_LEGIVEL()
    {
        var (db, tx, s, c) = await PrepararAsync("tenant-aplicar");
        using var _1 = db; using var _2 = tx;

        var vizinha = await Semeador.TenantAsync(db, "etiquetas-aplicar-vizinha");
        var daVizinha = new Etiqueta { EmpresaId = vizinha.Id, Nome = "Da vizinha" };
        db.Etiquetas.Add(daVizinha);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.AplicarAsync(c.Contato.Id, [daVizinha.Id], default));
    }

    [Fact]
    public async Task CONTATO_QUE_NAO_EXISTE_DA_MENSAGEM_LEGIVEL()
    {
        var (db, tx, s, _) = await PrepararAsync("contato-fantasma");
        using var _1 = db; using var _2 = tx;

        await Assert.ThrowsAsync<RegraDeNegocioException>(
            () => s.AplicarAsync(999_999, [], default));
    }

    [Fact]
    public async Task APLICAR_GRAVA_QUEM_MARCOU()
    {
        var (db, tx, s, c) = await PrepararAsync("quem-marcou");
        using var _1 = db; using var _2 = tx;

        var a = await s.CriarAsync(new NovaEtiqueta("Revendedor", null), default);
        await s.AplicarAsync(c.Contato.Id, [a], default);
        db.ChangeTracker.Clear();

        var marcacao = await db.ContatosEtiquetas.AsNoTracking().SingleAsync();
        Assert.Equal(c.Dono.Id, marcacao.CriadoPor);
    }

    // ==================================================================== contagem de uso
    /// <summary>⚠️ A CONTAGEM DA LISTA E A DO IMPACTO SÃO LIDAS COM SEGUNDOS DE DIFERENÇA pela
    /// mesma pessoa: uma na linha da etiqueta, a outra na confirmação de apagar.
    ///
    /// Se divergirem — "Urgente · 3 contatos" e logo em seguida "remover de 1" —, o dono não
    /// conclui "houve uma mudança no meio". Conclui que o sistema não sabe o que está dizendo, e é
    /// o pior tipo de defeito num produto que vende controle de dados.
    ///
    /// É a mesma lição do menu de pipelines: o que impede duas contas de divergirem não é
    /// disciplina, é um teste exigindo que deem o mesmo número.</summary>
    [Fact]
    public async Task A_CONTAGEM_DA_LISTA_BATE_COM_O_IMPACTO()
    {
        var (db, tx, s, c) = await PrepararAsync("contagem-impacto");
        using var _1 = db; using var _2 = tx;

        var id = await s.CriarAsync(new NovaEtiqueta("Revendedor", null), default);

        // Três contatos marcados, e um deles PERDIDO — a contagem é crua de propósito: a etiqueta
        // sai dele também quando for apagada.
        var outros = new List<long> { c.Contato.Id };
        for (var i = 0; i < 2; i++)
        {
            var extra = new Contato
            {
                EmpresaId = c.Id,
                Nome = $"Extra {i}",
                Telefone = $"55849333300{i:D2}",
                EtapaId = c.PrimeiraEtapa.Id,
                OrdemKanban = 10m + i,
                PerdidoEm = i == 0 ? DateTime.UtcNow : null,
                MotivoPerda = i == 0 ? "Sem interesse" : null
            };
            db.Contatos.Add(extra);
            await db.SaveChangesAsync();
            outros.Add(extra.Id);
        }

        foreach (var contatoId in outros)
        {
            await s.AplicarAsync(contatoId, [id], default);
            db.ChangeTracker.Clear();
        }

        var naLista = (await s.ListarAsync(null, OrdemEtiqueta.Nome, default))
            .Single(e => e.Id == id).Contatos;
        var noImpacto = await s.ImpactoAsync(id, default);

        Assert.Equal(naLista, noImpacto);

        // E o número não é trivialmente zero dos dois lados — senão o teste passaria sem provar
        // nada. São os três, o perdido incluído.
        Assert.Equal(3, naLista);
    }

    [Fact]
    public async Task ETIQUETA_SEM_USO_CONTA_ZERO()
    {
        var (db, tx, s, _) = await PrepararAsync("sem-uso");
        using var _1 = db; using var _2 = tx;

        var id = await s.CriarAsync(new NovaEtiqueta("Nunca usada", null), default);

        Assert.Equal(0, (await s.ListarAsync(null, OrdemEtiqueta.Nome, default))
            .Single(e => e.Id == id).Contatos);
        Assert.Equal(0, await s.ImpactoAsync(id, default));
    }

    [Fact]
    public async Task A_CONTAGEM_NAO_SOMA_MARCACAO_DE_OUTRA_EMPRESA()
    {
        var (db, tx, s, c) = await PrepararAsync("contagem-tenant");
        using var _1 = db; using var _2 = tx;

        var id = await s.CriarAsync(new NovaEtiqueta("Revendedor", null), default);
        await s.AplicarAsync(c.Contato.Id, [id], default);

        var vizinha = await Semeador.TenantAsync(db, "etiquetas-contagem-vizinha");
        var daVizinha = new Etiqueta { EmpresaId = vizinha.Id, Nome = "Revendedor" };
        db.Etiquetas.Add(daVizinha);
        await db.SaveChangesAsync();
        db.ContatosEtiquetas.Add(new ContatoEtiqueta
        {
            EmpresaId = vizinha.Id, ContatoId = vizinha.Contato.Id, EtiquetaId = daVizinha.Id
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(1, (await s.ListarAsync(null, OrdemEtiqueta.Nome, default))
            .Single(e => e.Id == id).Contatos);
    }

    // ==================================================================== ordem por uso
    [Fact]
    public async Task ORDEM_POR_USO_POE_A_MAIS_USADA_PRIMEIRO()
    {
        var (db, tx, s, c) = await PrepararAsync("ordem-uso");
        using var _1 = db; using var _2 = tx;

        // "Alfa" vem antes de "Zulu" no alfabeto e é a MENOS usada: se a ordenação caísse para
        // nome, o teste passaria por acaso.
        var alfa = await s.CriarAsync(new NovaEtiqueta("Alfa", null), default);
        var zulu = await s.CriarAsync(new NovaEtiqueta("Zulu", null), default);

        await s.AplicarAsync(c.Contato.Id, [zulu], default);
        db.ChangeTracker.Clear();

        var lista = await s.ListarAsync(null, OrdemEtiqueta.Uso, default);
        Assert.Equal(["Zulu", "Alfa"], lista.Select(e => e.Nome));
        _ = alfa;
    }

    [Fact]
    public async Task EMPATE_NO_USO_DESEMPATA_POR_NOME()
    {
        // Numa lista de sessenta, a maioria empata em zero — sem o desempate elas sairiam na
        // ordem física do Postgres, que muda sozinha.
        var (db, tx, s, _) = await PrepararAsync("empate-uso");
        using var _1 = db; using var _2 = tx;

        foreach (var nome in new[] { "Zulu", "Alfa", "Mike" })
            await s.CriarAsync(new NovaEtiqueta(nome, null), default);

        var lista = await s.ListarAsync(null, OrdemEtiqueta.Uso, default);
        Assert.Equal(["Alfa", "Mike", "Zulu"], lista.Select(e => e.Nome));
    }

    // ====================================================================
    private async Task<(NexoraDbContext, IDbContextTransaction, ServicoEtiquetas, Cenario)>
        PrepararAsync(string sufixo)
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, $"etiquetas-{sufixo}");
        ctx.EmpresaId = cenario.Id;
        ctx.UsuarioId = cenario.Dono.Id;
        ctx.Papel = "dono";

        return (db, tx, new ServicoEtiquetas(db, ctx), cenario);
    }
}
