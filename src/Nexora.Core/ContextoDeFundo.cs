namespace Nexora.Core;

/// <summary>===================== A EMPRESA QUE UM JOB ASSUMIU =====================
///
/// Fora de uma requisição não há claim nenhum: `EmpresaId` vale 0, o query filter global compara
/// com 0 e TODA consulta volta vazia, em silêncio. O jeito que este projeto usava até aqui era
/// `IgnoreQueryFilters()` mais um `Where` por empresa — bom para quem faz DUAS ou TRÊS consultas
/// (o motor de webhooks, o follow-up).
///
/// ⚠️ NÃO SERVE PARA REAPROVEITAR UM SERVIÇO INTEIRO. O processamento da importação em segundo
/// plano roda exatamente o mesmo código que o botão roda — dezenas de consultas, todas escritas
/// para um tenant. Reescrevê-las com `IgnoreQueryFilters` seria uma SEGUNDA CÓPIA da gravação, e a
/// que ninguém olha: a do botão, testada, e a do job, que só roda com arquivo grande.
///
/// Então o job ASSUME a empresa da importação que ele pegou, e daí para baixo tudo funciona como
/// numa requisição daquele cliente — inclusive o filtro que protege o isolamento.
///
/// ⚠️ QUEM ESCREVE AQUI É O JOB, NA RODADA DELE, e ninguém mais. Escopo por rodada: o objeto é
/// `Scoped`, e cada rodada cria o seu. Uma requisição HTTP nunca chega a ler isto — o contexto só
/// cai para cá quando não há usuário autenticado no `HttpContext`.
/// ======================================================================</summary>
public sealed class ContextoDeFundo
{
    public long EmpresaId { get; set; }

    /// <summary>Quem PEDIU o trabalho — a autoria do que o job gravar é dela, não "do sistema".</summary>
    public long UsuarioId { get; set; }

    public void Assumir(long empresaId, long usuarioId)
    {
        EmpresaId = empresaId;
        UsuarioId = usuarioId;
    }
}
