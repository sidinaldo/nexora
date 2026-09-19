import { Permissao } from '../modelos';

/** ===================== SÓ PARA OS TESTES =====================
 *  O que o SERVIDOR manda no login para cada papel (`Seguranca.Permissoes.NaApiPara`), para as
 *  fixtures montarem uma sessão realista.
 *
 *  ⚠️ É DADO DE TESTE, NÃO REGRA. O painel de verdade nunca deduz permissão do papel — ele só lê a
 *  lista que chega (`AuthServico.pode`). Esta tabela espelha `PermissoesTests.CADA_PAPEL_PODE_O_QUE_JA_PODIA`
 *  no backend; se as duas divergirem, é o teste de lá que diz qual está certa.
 *  ============================================================ */
export const PERMISSOES_DE: Record<'dono' | 'gestor' | 'vendedor', Permissao[]> = {
  dono: [
    'configurar_empresa', 'gerenciar_equipe', 'importar_contatos', 'cancelar_venda',
    'ver_historico', 'anonimizar_contato', 'cadastrar_feriado', 'ver_numeros_da_equipe'
  ],
  gestor: [
    'importar_contatos', 'cancelar_venda', 'ver_historico', 'anonimizar_contato',
    'cadastrar_feriado', 'ver_numeros_da_equipe'
  ],
  vendedor: []
};
