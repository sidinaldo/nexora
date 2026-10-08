import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import { PaginaComTotal } from '../modelos';

/** As duas abas, e elas respondem perguntas diferentes sobre eixos de tempo diferentes:
 *
 *    `parados`   → há N dias ninguém se fala, e o negócio está ABERTO;
 *    `perdidos`  → há N dias perdemos, e a pergunta é se vale uma nova tentativa.
 *
 *  ⚠️ SÃO DISJUNTAS: quem tem negócio aberto nunca aparece em "perdidos", mesmo tendo perdido
 *  noutro funil — ele está sendo trabalhado. */
export type AbaDeLeads = 'parados' | 'perdidos';

/** ===================== LPA-1 · QUEM PAROU DE SER TRABALHADO =====================
 *
 *  ⚠️ `negociacaoId`, `pipelineNome` e `etapaNome` SÃO NULOS quando o contato nunca virou negócio
 *  — e esse é o lead frio mais comum, não uma falha de dado. A tela escreve travessão; tratar como
 *  erro esconderia justamente quem mais precisa aparecer.
 *
 *  ⚠️ A GRANULARIDADE É A NEGOCIAÇÃO ABERTA, não o contato. Uma pessoa com negócio aberto em dois
 *  funis rende duas linhas, e é o certo: os dois podem estar parados por motivos diferentes.
 *
 *  ⚠️ ESTES TIPOS NÃO SE RECOPIAM NA TELA. Redeclarar interface do serviço no componente já causou
 *  bug aqui — o `/opcoes` ganhou um campo e a cópia ficou com a forma antiga.
 *  ============================================================================================ */
export interface LeadParado {
  contatoId: number;
  nome: string;
  telefone: string;
  origem: string;
  responsavelId: number | null;
  responsavelNome: string | null;
  negociacaoId: number | null;
  pipelineNome: string | null;
  etapaNome: string | null;
  valor: number | null;
  paradoDesde: string;
  diasParado: number;
  /** Meses de calendário completos parado, contados no servidor (AUD-XX). */
  mesesParado: number;

  /** ⚠️ SÓ A ABA "PERDIDOS" PREENCHE, e é a primeira informação de quem vai reabrir: "perdemos
   *  por preço" e "perdemos por prazo" levam a abordagens diferentes, e reabrir sem ler isso é
   *  repetir a conversa que falhou. Em "Parados" é sempre nulo — não houve perda. */
  motivoPerda: string | null;

  /** As etiquetas do NEGÓCIO da linha, em ordem de nome; vazia sem negócio. Faltava, e o efeito de
   *  "Aplicar etiqueta" não aparecia em lugar nenhum da tela — o operador achava que não tinha
   *  funcionado. Opcional porque o servidor antigo não manda. */
  etiquetas?: { id: number; nome: string; cor: string }[];
}

/** A página comum (AUD-XX, #21): o total e as páginas vêm prontos do servidor. */
export type PaginaLeadsParados = PaginaComTotal<LeadParado>;

/** As janelas que o servidor aceita. Lista fechada no `JanelasDeParada`: qualquer outro número
 *  volta 400. */
export type JanelaDeParada = 15 | 30 | 60 | 90;

/** O recorte inteiro, num objeto só.
 *
 *  ⚠️ TODOS MENOS `origem` SÃO DA NEGOCIAÇÃO, e ligar qualquer um deles esconde quem não tem
 *  negócio aberto — o lead que ninguém abriu não está em funil nenhum. A tela avisa em vez de
 *  deixar o operador concluir que a lista encolheu sozinha. */
export interface FiltroLeadsParados {
  aba: AbaDeLeads;
  dias: JanelaDeParada;
  pagina: number;
  responsavelId: number | null;
  pipelineId: number | null;
  etapaId: number | null;
  origem: string | null;
  etiquetaId: number | null;
  valorMin: number | null;
  valorMax: number | null;
}

/** O lembrete em lote.
 *
 *  ⚠️ `contatoIds`, NÃO `negociacaoIds`. A tabela mostra uma linha por negociação aberta, mas o
 *  lembrete é do CONTATO — quem tem negócio em dois funis recebe uma tarefa, não duas. A tela
 *  seleciona por contato por essa razão.
 *
 *  ⚠️ NÃO EXISTE CAMPO DE MENSAGEM, e é de propósito: o WhatsApp roda via Baileys e disparo em
 *  massa queima o número do cliente. Isto cria TAREFA para o vendedor, nunca envio. */
export interface LembreteEmLote {
  contatoIds: number[];
  dataAlvo: string;
  titulo: string;
  observacao: string | null;
}

/** A etiqueta em lote.
 *
 *  ⚠️ `negociacaoIds`, NÃO `contatoIds` — o oposto do lembrete, e de propósito. A etiqueta é do
 *  NEGÓCIO: a mesma pessoa pode ter dois negócios abertos, e marcar os dois atribuiria à
 *  reativação a venda do outro quando ela fosse ganha.
 *
 *  ⚠️ O SERVIDOR ADICIONA, NÃO SUBSTITUI, ao contrário do
 *  `PUT /api/etiquetas/negociacoes/{id}/etiquetas`, cujo corpo é o conjunto final. Em lote,
 *  substituir apagaria as outras etiquetas de cinquenta cards de uma vez. */
export interface EtiquetaEmLote {
  negociacaoIds: number[];
  etiquetaId: number;
}

/** ⚠️ `pulados` É SEPARADO DE `falhou` porque um não é problema e o outro é: pulado é quem já
 *  tinha lembrete pendente — não ganha outro, senão o vendedor recebe a mesma tarefa todo dia. */
export interface ResultadoEmLote {
  criados: number;
  pulados: number;
  falhou: number;
}

/** O que a reativação rendeu.
 *
 *  ⚠️ A JANELA É SOBRE A MARCA, NÃO SOBRE A VENDA. "Das vendas deste mês, quantas tinham sido
 *  marcadas" é outra pergunta, e esconderia as reativações ainda em andamento — que no primeiro
 *  mês de uma campanha são quase tudo. */
export interface Reativacao {
  marcados: number;
  ganhos: number;
  valorGanho: number;
  /** ganhos ÷ marcados, de 0 a 100 com 2 casas, PRONTO do servidor (AUD-XX). Null quando nada foi
   *  marcado: a tela mostra "—", e não "0%". */
  aproveitamentoPercentual: number | null;
}

@Injectable({ providedIn: 'root' })
export class LeadsParadosServico {
  private http = inject(HttpClient);

  readonly janelas: JanelaDeParada[] = [15, 30, 60, 90];

  /** Cinquenta por página porque a tela existe para AGIR em lote sobre o que está nela — vinte
   *  obrigaria a paginar no meio de uma seleção. */
  readonly porPagina = 50;

  listar(f: FiltroLeadsParados): Observable<PaginaLeadsParados> {
    let p = new HttpParams()
      .set('aba', f.aba)
      .set('dias', f.dias)
      .set('pagina', f.pagina)
      .set('tamanho', this.porPagina);

    // ⚠️ SÓ O QUE FOI PREENCHIDO ENTRA, e o teste é `!= null`, não truthy: com truthy, o id 0 e o
    // valor 0 sumiriam da query string. Mandar o parâmetro vazio é pior ainda — o servidor tenta
    // interpretar string vazia e devolve 400.
    const opcionais: [string, unknown][] = [
      ['responsavelId', f.responsavelId],
      ['pipelineId', f.pipelineId],
      ['etapaId', f.etapaId],
      ['origem', f.origem],
      ['etiquetaId', f.etiquetaId],
      ['valorMin', f.valorMin],
      ['valorMax', f.valorMax]
    ];

    for (const [chave, valor] of opcionais) {
      if (valor !== null && valor !== undefined && valor !== '') p = p.set(chave, String(valor));
    }

    return this.http.get<PaginaLeadsParados>(`${API}/leads-parados`, { params: p });
  }

  /** ⚠️ O TETO DO SERVIDOR É `porPagina`, e por isso "selecionar tudo" nesta tela sempre cabe em
   *  uma chamada. Mudar um dos dois números sem o outro deixa a tela oferecendo uma seleção que o
   *  servidor recusa com 400. */
  criarLembretes(pedido: LembreteEmLote): Observable<ResultadoEmLote> {
    return this.http.post<ResultadoEmLote>(`${API}/leads-parados/lembretes`, pedido);
  }

  /** ⚠️ `contatoIds`, como o lembrete: reabrir é do CONTATO. A aba mostra uma linha por PERDA, e
   *  quem perdeu em dois funis aparece duas vezes — o servidor deduplica, e a tela conta uma. */
  /** ⚠️ `negociacaoIds`, como a etiqueta: a atribuição que os relatórios leem é
   *  `negociacoes.responsavel_id`. O servidor muda as TRÊS colunas de dono a partir dela — a da
   *  negociação, a do contato e a da conversa —, senão a lista diria Ana e a caixa diria Bruno.
   *
   *  `responsavelId` nulo devolve o lead ao bolo, sem dono. */
  redistribuir(negociacaoIds: number[], responsavelId: number | null): Observable<ResultadoEmLote> {
    return this.http.post<ResultadoEmLote>(
      `${API}/leads-parados/responsavel`, { negociacaoIds, responsavelId });
  }

  reabrir(contatoIds: number[]): Observable<ResultadoEmLote> {
    return this.http.post<ResultadoEmLote>(
      `${API}/leads-parados/reabrir`, { contatoIds });
  }

  aplicarEtiqueta(pedido: EtiquetaEmLote): Observable<ResultadoEmLote> {
    return this.http.post<ResultadoEmLote>(`${API}/leads-parados/etiquetas`, pedido);
  }

  reativacao(
    etiquetaId: number, de: string, ate: string, responsavelId: number | null
  ): Observable<Reativacao> {
    let p = new HttpParams().set('etiquetaId', etiquetaId).set('de', de).set('ate', ate);

    // Mesmo teste `!= null` dos filtros da lista: com truthy, o id 0 sumiria da query string.
    if (responsavelId !== null) p = p.set('responsavelId', responsavelId);

    return this.http.get<Reativacao>(`${API}/leads-parados/reativacao`, { params: p });
  }
}
