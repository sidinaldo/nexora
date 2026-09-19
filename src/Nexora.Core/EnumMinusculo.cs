using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexora.Core;

/// <summary>Enum no JSON com o MESMO rótulo que o banco usa: `Nova` → `"nova"`,
/// `AguardandoMapeamento` → `"aguardando_mapeamento"`.
///
/// ===================== POR QUE EXISTE =====================
/// O conversor global da API (`OpcoesJson`) é `JsonStringEnumConverter` SEM política de nome,
/// então manda o membro como está no C#: `"Nova"`. O resto do projeto contorna isso na projeção,
/// com `.ToString().ToLower()` para um campo `string`. `LinhaImportada.Situacao` não contornou —
/// o enum ia direto — e a tela comparava com `'nova'`.
///
/// Resultado, achado em revisão: TODA linha da prévia de importação aparecia como "fora", em
/// vermelho, inclusive as boas. O dono via "612 contatos novos" em cima de uma lista inteira
/// marcada como recusada. Os testes do painel não pegaram porque os mocks já vinham em
/// minúscula — mockavam o contrato que a tela ESPERAVA, não o que a API MANDAVA.
///
/// ⚠️ SNAKE_CASE, E NÃO CAMELCASE: é o rótulo que o Npgsql grava no enum nativo do Postgres.
/// A tela, o banco e o log passam a dizer a mesma palavra para o mesmo estado.
///
/// ⚠️ NA PROPRIEDADE (`[property: JsonConverter(...)]`), E NÃO NO TIPO. Um conversor na coleção
/// de opções vence o atributo do tipo — e o global é justamente uma fábrica para todo enum.
/// Só o atributo de propriedade passa na frente dele.
///
/// `allowIntegerValues: false` fecha a porta de entrada também: um `2` no JSON não vira membro
/// nenhum por posição.
/// ==========================================================</summary>
public sealed class EnumMinusculo<T>()
    : JsonStringEnumConverter<T>(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false)
    where T : struct, Enum;
