import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApplicationRef, provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { PERMISSOES_DE } from '../../nucleo/seguranca/permissoes-de-teste';
import { LeadParado } from '../../nucleo/servicos/leads-parados.servico';
import { LARGURA_CELULAR } from '../telas-do-painel';
import { LeadsParados } from './leads-parados';

/** ===================== POR QUE ESTE ARQUIVO EXISTE =====================
 *  `paginas.celular.spec.ts` monta TODA tela em 390px — inclusive esta —, mas duas partes do lote
 *  escapam daquele laço, e as duas pelo mesmo motivo: ele só faz `detectChanges`.
 *
 *    · a BARRA DE AÇÕES só renderiza com algo selecionado, e lá nada é selecionado;
 *    · o MODAL só renderiza aberto, e lá nada é aberto — e, mesmo aberto, o laço ISENTA o que
 *      está dentro de `.overlay` (`FLUTUAM_DE_PROPOSITO`), porque flutuar é o que um modal faz.
 *
 *  Resultado: a barra onde se confirma a ação e o formulário com data e observação nunca foram
 *  medidos em largura de celular. Este arquivo mede. Mesma razão de `equipe.celular.spec.ts`.
 *
 *  ⚠️ A JANELA REAL É ~504px, NÃO 390px (o Chrome headless trava a largura num piso, documentado
 *  em `karma.conf.js`). A largura de 390px vem da CAIXA, e é legítima para o `.modal` porque ele
 *  tem `max-width: 460px` — em 390px é o que o aparelho mostra de verdade.
 *  ======================================================================= */
describe('leads parados no celular — a barra do lote e o modal', () => {
  let fixture: ComponentFixture<LeadsParados>;
  let c: LeadsParados;
  let http: HttpTestingController;
  let palco: HTMLElement;

  /** Nome e telefone longos de propósito: eles dividem a linha com a caixinha de seleção. */
  function lead(over: Partial<LeadParado> = {}): LeadParado {
    return {
      contatoId: 7, nome: 'Maria Aparecida de Albuquerque Nogueira',
      telefone: '5584999990000', origem: 'instagram',
      responsavelId: 3, responsavelNome: 'Rafael Monteiro de Albuquerque',
      negociacaoId: 41, pipelineNome: 'Vendas consultivas', etapaNome: 'Proposta enviada',
      valor: 125000, paradoDesde: '2026-06-01T10:00:00Z', diasParado: 97,
      motivoPerda: null,
      ...over
    };
  }

  function montar() {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });

    TestBed.inject(AuthServico).aplicarLogin({
      token: 't',
      usuario: {
        id: 1, nome: 'Ana Souza', email: 'ana@x.com', papel: 'dono',
        permissoes: PERMISSOES_DE.dono, empresaNome: 'Padaria do Bairro'
      }
    } as never);

    palco = document.createElement('div');
    palco.style.width = `${LARGURA_CELULAR}px`;
    palco.style.overflow = 'hidden';
    document.body.appendChild(palco);

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(LeadsParados);
    c = fixture.componentInstance;
    palco.appendChild(fixture.nativeElement);

    fixture.detectChanges();

    for (const r of http.match(() => true)) {
      const url = r.request.url;
      if (url.endsWith('/opcoes')) r.flush({ responsaveis: [], etapas: [], motivosPerda: [] });
      else if (url.endsWith('/etiquetas')) r.flush([]);
      // ⚠️ `negociacaoId` DISTINTO: a chave da selecao e a da negociacao, e duas linhas com o
      // mesmo id sao UMA para ela — a tabela mostraria duas e "marcar todos" marcaria uma.
      else r.flush({
        itens: [lead(), lead({ contatoId: 8, nome: 'Bruno Lima', negociacaoId: 42 })],
        total: 2
      });
    }

    fixture.detectChanges();
    TestBed.inject(ApplicationRef).tick();
  }

  function raiz(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function marcarTudo() {
    raiz().querySelector<HTMLElement>('thead .sel input')!.click();
    fixture.detectChanges();
    TestBed.inject(ApplicationRef).tick();
  }

  function abrirModal(
    qual: 'criar-lembretes' | 'aplicar-etiqueta' | 'redistribuir' = 'criar-lembretes'
  ) {
    marcarTudo();
    raiz().querySelector<HTMLElement>(`.${qual}`)!.click();
    fixture.detectChanges();
    TestBed.inject(ApplicationRef).tick();
  }

  afterEach(() => {
    palco?.remove();
    http.verify();
    TestBed.resetTestingModule();
  });

  /** ⚠️ MEDE O PALCO, NÃO A PÁGINA. A tabela tem sete colunas e rola de propósito dentro de
   *  `.tabela-rolagem`; o que não pode andar de lado é a caixa que contém a tela. */
  function transbordo(): number {
    return palco.scrollWidth - palco.clientWidth;
  }

  it('A BARRA DO LOTE NÃO ANDA DE LADO', () => {
    montar();
    marcarTudo();

    expect(raiz().querySelector('.barra-lote')).withContext('a barra abriu').not.toBeNull();
    expect(transbordo())
      .withContext(`a barra passa ${transbordo()}px de ${LARGURA_CELULAR}px`)
      .toBeLessThanOrEqual(1);
  });

  /** ⚠️ NÃO É UM TESTE DE `flex-wrap`. Medi: trocar `wrap` por `nowrap` não derruba nada, porque
   *  os botões têm `flex-shrink` e ENCOLHEM em vez de sair da caixa. O que se guarda aqui é o
   *  invariante que importa — o "Desmarcar" alcançável —, e ele cai quando alguém impede o
   *  encolhimento (um `flex-shrink: 0`, um `white-space: nowrap`, uma largura fixa). */
  it('OS BOTÕES DA BARRA NÃO SAEM DA CAIXA', () => {
    montar();
    marcarTudo();

    const caixa = palco.getBoundingClientRect();

    for (const b of raiz().querySelectorAll<HTMLElement>('.barra-lote button')) {
      const r = b.getBoundingClientRect();
      expect(r.right)
        .withContext(`"${b.textContent?.trim()}" termina em ${Math.round(r.right)}px`)
        .toBeLessThanOrEqual(caixa.right + 1);
    }
  });

  /** ⚠️ A ABA "PERDIDOS" TEM UMA COLUNA A MAIS, e de TEXTO LIVRE: o motivo da perda e o que o
   *  vendedor escreveu, de qualquer tamanho. O laco compartilhado nao ve — ele monta a aba PADRAO.
   *
   *  ⚠️ E NAO E UM TESTE DO `white-space` DO `.motivo`. Sabotei para `nowrap` e NADA CAIU: a
   *  tabela vive dentro de `.tabela-rolagem`, que tem `overflow-x: auto` e ABSORVE a largura — o
   *  container rola, a pagina nao. O `max-width` com quebra de linha e escolha de LEITURA (uma
   *  frase de duas linhas numa celula se le; uma de 600px nao), nao guarda de transbordo.
   *
   *  O que este teste guarda e que a coluna a mais continua dentro daquele container, e nao
   *  escapa dele para a pagina — a mesma garantia da aba "parados", numa configuracao a mais. */
  it('A ABA PERDIDOS NÃO ANDA DE LADO, COM MOTIVO LONGO', () => {
    montar();

    raiz().querySelector<HTMLElement>('.abas-topo .aba:nth-child(2)')!.click();
    fixture.detectChanges();

    http.expectOne(r => r.url.includes('/leads-parados')).flush({
      itens: [
        lead({
          negociacaoId: 41,
          motivoPerda: 'Disse que o concorrente ofereceu parcelamento em doze vezes sem juros e '
            + 'um prazo de entrega menor, e que vai fechar com eles ainda esta semana'
        })
      ],
      total: 1
    });
    fixture.detectChanges();
    TestBed.inject(ApplicationRef).tick();

    expect(raiz().querySelector('td.motivo')).withContext('a coluna existe').not.toBeNull();
    expect(transbordo())
      .withContext(`a aba perdidos passa ${transbordo()}px de ${LARGURA_CELULAR}px`)
      .toBeLessThanOrEqual(1);
  });

  it('O MODAL DO LEMBRETE NÃO ANDA DE LADO', () => {
    montar();
    abrirModal();

    expect(raiz().querySelector('.overlay .modal')).withContext('o modal abriu').not.toBeNull();
    expect(transbordo())
      .withContext(`o modal passa ${transbordo()}px de ${LARGURA_CELULAR}px`)
      .toBeLessThanOrEqual(1);
  });

  /** ⚠️ O LAÇO COMPARTILHADO NÃO ABRE O BLOCO DA MÉTRICA: ele só faz `detectChanges`, e o bloco
   *  começa fechado. São quatro números grandes com rótulo em texto corrido — em 390px eles têm
   *  de empilhar, e a grade de `auto-fit` é quem faz isso sem media query por quantidade. */
  it('O BLOCO DA MÉTRICA NÃO ANDA DE LADO COM OS QUATRO NÚMEROS', () => {
    montar();

    raiz().querySelector<HTMLElement>('.abre-metrica')!.click();
    c.metricaEtiqueta.set(5);
    c.carregarMetrica();
    fixture.detectChanges();

    http.expectOne(r => r.url.endsWith('/leads-parados/reativacao'))
      // Números largos de propósito: é o que estoura.
      .flush({ marcados: 1480, ganhos: 376, valorGanho: 1234567.89 });
    fixture.detectChanges();
    TestBed.inject(ApplicationRef).tick();

    expect(raiz().querySelectorAll('.numeros .numero').length).toBe(4);
    expect(transbordo())
      .withContext(`o bloco da métrica passa ${transbordo()}px de ${LARGURA_CELULAR}px`)
      .toBeLessThanOrEqual(1);
  });

  it('O MODAL DA ETIQUETA NÃO ANDA DE LADO', () => {
    // Ele tem um seletor de etiqueta e DUAS linhas de aviso em texto corrido — o aviso de quem
    // fica de fora e o de que a etiqueta e somada. E o tipo de bloco que estoura.
    montar();
    abrirModal('aplicar-etiqueta');

    expect(raiz().querySelector('#lote-etiqueta')).withContext('é o da etiqueta').not.toBeNull();
    expect(transbordo())
      .withContext(`o modal da etiqueta passa ${transbordo()}px de ${LARGURA_CELULAR}px`)
      .toBeLessThanOrEqual(1);
  });

  /** O modal de responsavel tem o seletor da equipe e DUAS linhas de texto corrido — a frase que
   *  explica que muda todo o painel e o aviso de quem fica de fora. O laco compartilhado nao abre
   *  modal nenhum. */
  it('O MODAL DE RESPONSÁVEL NÃO ANDA DE LADO', () => {
    montar();
    abrirModal('redistribuir');

    expect(raiz().querySelector('#lote-responsavel')).withContext('é o de responsável')
      .not.toBeNull();
    expect(transbordo())
      .withContext(`o modal de responsável passa ${transbordo()}px de ${LARGURA_CELULAR}px`)
      .toBeLessThanOrEqual(1);
  });

  it('O MODAL USA O CONTRATO DE CLASSES DO DESIGN SYSTEM', () => {
    // `design-system.celular.spec.ts` garante que `.overlay`/`.modal`/`.modal-corpo` mantêm o topo
    // alcançável e o corpo rolando em tela baixa. Renomear as classes aqui sairia daquela garantia
    // calado.
    montar();
    abrirModal();

    expect(raiz().querySelector('.overlay > .modal')).not.toBeNull();
    expect(raiz().querySelector('.modal .modal-corpo')).not.toBeNull();
  });

  /** ⚠️ O LAÇO COMPARTILHADO NÃO PEGA ISTO: ele isenta tudo dentro de `.overlay`. Dentro de um
   *  corpo que ROLA (`.modal-corpo` tem `overflow-y: auto`), um elemento posicionado não acompanha
   *  a rolagem: ele fica onde está e cobre o que a pessoa está lendo. */
  it('NADA DENTRO DO MODAL FLUTUA', () => {
    montar();
    abrirModal();

    const flutuantes = [...raiz().querySelectorAll('.modal *')]
      .filter(e => ['fixed', 'sticky'].includes(getComputedStyle(e).position))
      .map(e => `${e.tagName.toLowerCase()}.${[...e.classList].join('.')}`);

    expect(flutuantes).toEqual([]);
  });

  /** ===================== A CAIXINHA É O ALVO MAIS PROVÁVEL DE ERRO DE TOQUE =====================
   *
   *  ⚠️ Um `<input type="checkbox">` nu tem ~13px. Numa lista onde a ação seguinte cria lembrete
   *  para quem está marcado, errar o toque marca o lead errado — e o `.sel` é a célula mais
   *  estreita da tabela, logo ao lado de um link que NAVEGA para outra tela.
   *  ============================================================================================= */
  it('A CAIXINHA DE SELEÇÃO TEM ALVO DE TOQUE SUFICIENTE', () => {
    montar();

    const alvos = [...raiz().querySelectorAll<HTMLElement>('tbody .sel label')];

    // ⚠️ A CONTAGEM VEM PRIMEIRO. Sem ela, tirar o `<label>` deixa a lista vazia, o laço não
    // afirma nada e o teste passa verde — medi essa sabotagem e ela não derrubava nada.
    expect(alvos.length).withContext('uma caixinha por linha, dentro de um label').toBe(2);

    for (const alvo of alvos) {
      const r = alvo.getBoundingClientRect();

      expect(Math.min(r.width, r.height))
        .withContext(`o alvo mede ${Math.round(r.width)}×${Math.round(r.height)}px`)
        .toBeGreaterThanOrEqual(40);
    }
  });
});
