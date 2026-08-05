namespace Nexora.Core.Servicos;

/// <summary>O que a semeadura criou (ou apagou).</summary>
public record ResumoSemente(
    int Contatos, int Conversas, int Mensagens, int Lembretes, int Usuarios, int Feriados);

/// <summary>DADOS FALSOS PARA DESENVOLVIMENTO. Popula o tenant logado com um cenário que
/// exercita TODAS as telas: caixa com conversas em vários estágios do semáforo, funil com as
/// cinco colunas cheias, contatos ganhos e perdidos, Meu Dia com ação atrasada, dashboard com
/// faturamento, equipe com papéis variados.
///
/// ===================== POR QUE ISTO EXISTE =====================
/// Tela vazia não se avalia. Cor de semáforo, quebra de linha em nome longo, coluna de kanban
/// que rola, paginação, estado "atrasado" — nada disso aparece num banco com um contato. E
/// digitar trinta contatos à mão a cada vez que o banco é recriado não acontece: a pessoa
/// testa com dois e o bug de layout aparece no cliente.
/// ==============================================================
///
/// TUDO que é criado leva a MARCA `semente-dev` (em `contatos.origem_detalhe` e no e-mail dos
/// usuários), e é só isso que `LimparAsync` apaga. Dado digitado à mão sobrevive.
///
/// ⚠️ Exposto SÓ em Development (ver DevController). Não existe em produção.</summary>
public interface IServicoSemente
{
    /// <summary>Popula o tenant logado. Limpa a semeadura anterior antes, para poder rodar
    /// várias vezes sem colidir no índice único de telefone.</summary>
    Task<ResumoSemente> SemearAsync(CancellationToken ct);

    /// <summary>Apaga SÓ o que foi semeado. Devolve quanto saiu.</summary>
    Task<ResumoSemente> LimparAsync(CancellationToken ct);
}
