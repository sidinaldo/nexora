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
      else r.flush({ itens: [lead(), lead({ contatoId: 8, nome: 'Bruno Lima' })], total: 2 });
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

  function abrirModal() {
    marcarTudo();
    raiz().querySelector<HTMLElement>('.criar-lembretes')!.click();
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

  it('O MODAL DO LEMBRETE NÃO ANDA DE LADO', () => {
    montar();
    abrirModal();

    expect(raiz().querySelector('.overlay .modal')).withContext('o modal abriu').not.toBeNull();
    expect(transbordo())
      .withContext(`o modal passa ${transbordo()}px de ${LARGURA_CELULAR}px`)
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
