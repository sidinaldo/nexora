using Nexora.Core.Entidades;
using Nexora.Core.Seguranca;
using Nexora.Infra.Persistencia;

namespace Nexora.Tests.Integracao;

/// <summary>Tudo que um tenant precisa ter para os testes de dominio: empresa, dono, conexao,
/// as 5 etapas, um contato, a negociacao aberta dele, a conversa e uma mensagem.
///
/// Semear dois destes e a base de todo teste de isolamento — o ponto e sempre o mesmo:
/// existe linha do OUTRO tenant no banco, e ela nao pode aparecer.</summary>
public sealed record Cenario(
    Empresa Empresa,
    Usuario Dono,
    Conexao Conexao,
    Pipeline Pipeline,
    IReadOnlyList<EtapaFunil> Etapas,
    Contato Contato,
    Negociacao Negociacao,
    Conversa Conversa,
    Mensagem Mensagem)
{
    public long Id => Empresa.Id;
    public EtapaFunil PrimeiraEtapa => Etapas[0];
}

public static class Semeador
{
    /// <summary>Monta um tenant completo. `sufixo` distingue e-mail, telefone e instancia, que
    /// sao unicos globalmente (ou por empresa) e colidiriam entre os dois cenarios.</summary>
    public static async Task<Cenario> TenantAsync(NexoraDbContext db, string sufixo)
    {
        var empresa = new Empresa { Nome = $"Empresa {sufixo}" };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();

        var dono = new Usuario
        {
            EmpresaId = empresa.Id,
            Nome = $"Dono {sufixo}",
            Email = $"dono-{sufixo}@exemplo.com",
            SenhaHash = HashSenha.Gerar("senha-de-teste-123"),
            Papel = PapelUsuario.Dono,
            Status = StatusUsuario.Ativo
        };
        var conexao = new Conexao
        {
            EmpresaId = empresa.Id,
            Nome = "Principal",
            InstanceName = $"inst-{sufixo}",
            Status = StatusConexao.Conectado,
            Numero = $"5584900000{Semente(sufixo) % 1000:D3}"
        };
        // A pipeline vem antes das etapas: elas apontam para ela por FK composta e precisam do id.
        // Sao TRES etapas aqui, nao as cinco do cadastro real — os testes que dependem da forma do
        // funil constroem a propria, e `EtapasDbTests` se recusa a fixar a contagem de proposito.
        var pipeline = new Pipeline
        {
            EmpresaId = empresa.Id,
            Nome = "Vendas",
            Ordem = 1,
            Padrao = true
        };
        db.Usuarios.Add(dono);
        db.Conexoes.Add(conexao);
        db.Pipelines.Add(pipeline);
        await db.SaveChangesAsync();

        var etapas = new List<EtapaFunil>
        {
            new() { EmpresaId = empresa.Id, PipelineId = pipeline.Id, Nome = "Novo Lead", Ordem = 1 },
            new() { EmpresaId = empresa.Id, PipelineId = pipeline.Id, Nome = "Proposta",  Ordem = 2 },
            new() { EmpresaId = empresa.Id, PipelineId = pipeline.Id, Nome = "Venda",     Ordem = 3, EGanho = true }
        };
        db.EtapasFunil.AddRange(etapas);
        await db.SaveChangesAsync();

        var contato = new Contato
        {
            EmpresaId = empresa.Id,
            Nome = $"Contato {sufixo}",
            Telefone = $"558490{Semente(sufixo) % 10_000_000:D7}",
            ResponsavelId = dono.Id
        };
        db.Contatos.Add(contato);
        await db.SaveChangesAsync();

        // A negociacao ABERTA do contato — e desde o E4e/4 e ela, e SO ela, que diz onde a pessoa
        // esta no funil. Um cenario sem negociacao e um contato que nao aparece em quadro nenhum.
        var negociacao = Negocio(contato, etapas[0]);
        db.Negociacoes.Add(negociacao);
        await db.SaveChangesAsync();

        var conversa = new Conversa
        {
            EmpresaId = empresa.Id,
            ContatoId = contato.Id,
            ConexaoId = conexao.Id,
            ResponsavelId = dono.Id,
            UltimaMensagemEm = DateTime.UtcNow
        };
        db.Conversas.Add(conversa);
        await db.SaveChangesAsync();

        var mensagem = new Mensagem
        {
            EmpresaId = empresa.Id,
            ConversaId = conversa.Id,
            ContatoId = contato.Id,
            ConexaoId = conexao.Id,
            InstanceName = conexao.InstanceName,
            Direcao = DirecaoMensagem.Entrada,
            WaMessageId = $"WA-{sufixo}-001",
            Texto = $"oi, sou o contato {sufixo}",
            RecebidaEm = DateTime.UtcNow
        };
        db.Mensagens.Add(mensagem);
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        return new Cenario(
            empresa, dono, conexao, pipeline, etapas, contato, negociacao, conversa, mensagem);
    }

    /// <summary>Semente ESTAVEL a partir do sufixo do cenario.
    ///
    /// ===================== POR QUE NAO `string.GetHashCode()` =====================
    /// Porque ele e ALEATORIZADO POR PROCESSO. O .NET semeia o hash de string com um valor
    /// sorteado no arranque (defesa contra hash-flooding), e a documentacao diz textualmente que
    /// o valor nao deve ser persistido nem comparado entre execucoes.
    ///
    /// Usado para montar telefone de teste, isso significa DADO DIFERENTE A CADA RODADA — e um
    /// teste que passa mil vezes e reprova na milesima, sem ninguem ter mexido em nada. Foi o que
    /// aconteceu: `Listar_busca_por_nome_e_por_digitos_do_telefone` procura por "(84) 98333" e o
    /// contato do cenario nasceu com 5584983332282, que contem os mesmos digitos. Duas linhas
    /// voltaram onde o teste esperava uma, e o CI ficou vermelho sem causa aparente.
    ///
    /// FNV-1a de 32 bits: cinco linhas, estavel entre processos, entre maquinas e entre versoes
    /// do runtime. O sufixo do cenario passa a produzir sempre o mesmo numero.
    ///
    /// ⚠️ E `Math.Abs(hash)` ainda tinha um segundo problema: `Math.Abs(int.MinValue)` LANCA
    /// `OverflowException`. Uma chance em 4 bilhoes de derrubar a suite inteira por aritmetica.
    /// A mascara abaixo nao tem esse caso.
    /// =============================================================================</summary>
    /// <summary>A negociacao aberta de um contato, para as fixtures.
    ///
    /// ⚠️ EXISTE PORQUE O E4e/4 TIROU A ETAPA DO CONTATO. Antes bastava `new Contato { EtapaId }`
    /// e o card aparecia; hoje o contato sozinho nao esta em funil nenhum, e cada teste que
    /// precisa de card precisa tambem de negociacao. Repetir o bloco em 28 arquivos garantiria
    /// que os 28 divergissem — foi exatamente assim que `RESPONDEM_ARRAY` acabou em quatro
    /// copias, duas delas erradas.
    ///
    /// Liga pela NAVEGACAO para servir tambem a quem grava contato e negociacao no mesmo
    /// `SaveChanges`.</summary>
    internal static Negociacao Negocio(
        Contato contato, EtapaFunil etapa, decimal ordem = 1000m, decimal? valor = null) => new()
    {
        EmpresaId = contato.EmpresaId,
        Contato = contato,
        PipelineId = etapa.PipelineId,
        EtapaId = etapa.Id,
        OrdemKanban = ordem,
        ResponsavelId = contato.ResponsavelId,
        Valor = valor,
        Status = StatusNegociacao.Aberta
    };

    internal static int Semente(string sufixo)
    {
        unchecked
        {
            var h = 2166136261u;
            foreach (var c in sufixo) { h ^= c; h *= 16777619u; }
            return (int)(h & 0x7FFFFFFF);
        }
    }
}
